using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Anthropic;
using AiSdk.Internal;
using AiSdk.OpenAI;

namespace AiSdk.Tests;

public class OpenAIResponsesTests
{
    private static (OpenAIProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "sk-test", HttpClient = new HttpClient(handler) }), handler);
    }

    private const string ResponseJson = """
        {"id":"resp_1","model":"gpt-5","created_at":1700000000,"status":"completed",
         "output":[
           {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Busco el clima"}]},
           {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"clima Santiago"}},
           {"type":"message","id":"msg_1","role":"assistant","content":[
              {"type":"output_text","text":"Hay 20 °C.","annotations":[{"type":"url_citation","url":"https://meteo.cl","title":"Meteo"}]}]},
           {"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Lima\"}"}
         ],
         "usage":{"input_tokens":30,"output_tokens":12,"input_tokens_details":{"cached_tokens":4},"output_tokens_details":{"reasoning_tokens":5}}}
        """;

    [Fact]
    public async Task MapsRequestAndParsesAllOutputItems()
    {
        var (openai, handler) = Create();
        handler.Json(ResponseJson);

        var result = await openai.Responses("gpt-5").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.System("sys"), ModelMessage.User(new TextPart("hola"), ImagePart.FromUrl("https://x/y.png"))],
            Tools =
            [
                new ToolDefinition("get_weather", "clima", JsonSchema.For<WeatherInput>()),
                OpenAITools.WebSearch(searchContextSize: "low").ToDefinition(),
                AnthropicTools.WebSearch().ToDefinition(),
            ],
            MaxOutputTokens = 500,
            ResponseFormat = ResponseFormat.Json(JsonSchema.For<WeatherInput>(), "weather"),
            ProviderOptions = OpenAIOptions.Create(reasoningEffort: "low", reasoningSummary: "auto"),
        });

        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
        Assert.Equal(500, (int?)body["max_output_tokens"]);
        Assert.Equal("system", (string?)body["input"]![0]!["role"]);
        Assert.Equal("input_image", (string?)body["input"]![1]!["content"]![1]!["type"]);
        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count); // la herramienta de Anthropic se omite
        Assert.Equal("function", (string?)tools[0]!["type"]);
        Assert.Equal("web_search", (string?)tools[1]!["type"]);
        Assert.Equal("low", (string?)tools[1]!["search_context_size"]);
        Assert.Equal("json_schema", (string?)body["text"]!["format"]!["type"]);
        Assert.Equal("low", (string?)body["reasoning"]!["effort"]);
        Assert.Contains(result.Warnings, w => w.Contains("anthropic"));

        Assert.Equal("Busco el clima", Assert.IsType<ReasoningPart>(result.Content[0]).Text);
        var search = Assert.IsType<ToolCallPart>(result.Content[1]);
        Assert.True(search.ProviderExecuted);
        Assert.Equal("web_search", search.ToolName);
        Assert.True(Assert.IsType<ToolResultPart>(result.Content[2]).ProviderExecuted);
        Assert.Equal("Hay 20 °C.", Assert.IsType<TextPart>(result.Content[3]).Text);
        Assert.Equal("https://meteo.cl", Assert.IsType<SourcePart>(result.Content[4]).Url);
        var call = Assert.IsType<ToolCallPart>(result.Content[5]);
        Assert.Equal("call_1", call.ToolCallId);
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(5, result.Usage.ReasoningTokens);
        Assert.Equal(4, result.Usage.CachedInputTokens);
    }

    [Fact]
    public async Task ReplaysReasoningFunctionCallsAndOutputs()
    {
        var (openai, handler) = Create();
        handler.Json(ResponseJson).Json("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"fin"}]}]}""");
        var model = openai.Responses("gpt-5");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Tool.Create<WeatherInput, string>("get_weather", "clima", i => "21°"), OpenAITools.WebSearch()],
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.Equal("fin", result.Text);
        var input = handler.Requests[1].Body["input"]!.AsArray();
        var types = input.Select(i => (string?)i!["type"] ?? (string?)i!["role"]).ToList();
        Assert.Equal(["user", "item_reference", "item_reference", "assistant", "function_call", "function_call_output"], types);
        Assert.Equal("rs_1", (string?)input[1]!["id"]);
        Assert.Equal("ws_1", (string?)input[2]!["id"]);
        Assert.Equal("call_1", (string?)input[5]!["call_id"]);
        Assert.Equal("21°", (string?)input[5]!["output"]);
    }

    [Fact]
    public async Task StoreFalseSendsEncryptedReasoning()
    {
        var (openai, handler) = Create();
        handler.Json("""{"status":"completed","output":[]}""");
        var reasoning = new ReasoningPart("resumen")
        {
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [OpenAIResponsesLanguageModel.ItemIdKey] = JsonSerializer.SerializeToElement("rs_9"),
                [OpenAIResponsesLanguageModel.EncryptedContentKey] = JsonSerializer.SerializeToElement("enc=="),
            },
        };

        await openai.Responses("gpt-5").GenerateAsync(new LanguageModelRequest
        {
            Messages =
            [
                ModelMessage.User("x"),
                ModelMessage.Assistant(
                    reasoning,
                    new ToolCallPart("ws_1", "web_search", JsonDocument.Parse("{}").RootElement)
                    {
                        ProviderExecuted = true,
                        ProviderMetadata = new Dictionary<string, JsonElement> { [OpenAIResponsesLanguageModel.ItemIdKey] = JsonSerializer.SerializeToElement("ws_1") },
                    }),
                ModelMessage.User("y"),
            ],
            ProviderOptions = OpenAIOptions.Create(store: false),
        });

        var body = handler.Requests[0].Body;
        Assert.Equal(3, body["input"]!.AsArray().Count);
        Assert.False((bool?)body["store"]);
        Assert.Equal("reasoning.encrypted_content", (string?)body["include"]![0]);
        var item = body["input"]![1]!;
        Assert.Equal("reasoning", (string?)item["type"]);
        Assert.Equal("enc==", (string?)item["encrypted_content"]);
        Assert.Equal("resumen", (string?)item["summary"]![0]!["text"]);
    }

    [Fact]
    public async Task StreamsResponsesEvents()
    {
        var (openai, handler) = Create();
        static string E(string json) => "data: " + json;
        handler.Sse(
            E("""{"type":"response.created","response":{"id":"resp_1","model":"gpt-5"}}"""),
            E("""{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","summary_index":0,"delta":"Pienso"}"""),
            E("""{"type":"response.output_item.done","item":{"type":"reasoning","id":"rs_1","summary":[]}}"""),
            E("""{"type":"response.output_text.delta","item_id":"msg_1","content_index":0,"delta":"Hola "}"""),
            E("""{"type":"response.output_text.delta","item_id":"msg_1","content_index":0,"delta":"mundo"}"""),
            E("""{"type":"response.output_text.annotation.added","annotation":{"type":"url_citation","url":"https://a.cl","title":"A"}}"""),
            E("""{"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather"}}"""),
            E("""{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":"}"""),
            E("""{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"\"Lima\"}"}"""),
            E("""{"type":"response.output_item.done","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"city\":\"Lima\"}"}}"""),
            E("""{"type":"response.output_item.done","item":{"type":"web_search_call","id":"ws_1","status":"completed","action":{"query":"q"}}}"""),
            E("""{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":7,"output_tokens":9}}}"""));

        var parts = new List<StreamPart>();
        await foreach (var p in openai.Responses("gpt-5").StreamAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }))
        {
            parts.Add(p);
        }

        var accumulator = new StreamAccumulator();
        parts.ForEach(accumulator.Add);
        var result = accumulator.Build();

        Assert.True((bool?)handler.Requests[0].Body["stream"]);
        Assert.Equal("Pienso", Assert.IsType<ReasoningPart>(result.Content[0]).Text);
        Assert.Equal("Hola mundo", Assert.IsType<TextPart>(result.Content[1]).Text);
        Assert.Equal("https://a.cl", Assert.IsType<SourcePart>(result.Content[2]).Url);
        Assert.Equal("Lima", Assert.IsType<ToolCallPart>(result.Content[3]).Input.GetProperty("city").GetString());
        Assert.True(Assert.IsType<ToolCallPart>(result.Content[4]).ProviderExecuted);
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(9, result.Usage.OutputTokens);
        Assert.Contains(parts, p => p is ToolInputDeltaPart { ToolCallId: "call_1" });
    }

    [Fact]
    public async Task ChatCompletionsTranslatesResponsesStyleOptions()
    {
        var (openai, handler) = Create();
        handler.Json("""{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""");

        await openai.Chat("gpt-5").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.User("x")],
            Tools = [OpenAITools.WebSearch().ToDefinition()],
            ProviderOptions = OpenAIOptions.Create(reasoningEffort: "high", store: false),
        });

        var body = handler.Requests[0].Body;
        Assert.Equal("high", (string?)body["reasoning_effort"]);
        Assert.Null(body["reasoning"]);
        Assert.Null(body["include"]);
        Assert.Null(body["tools"]); // las herramientas de servidor no existen en Chat Completions
    }

    [Fact]
    public void RegistryUsesResponsesForOpenAIAndChatForCompatibleServers()
    {
        Assert.IsType<OpenAIResponsesLanguageModel>(new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k" }).LanguageModel("gpt-5"));
        Assert.IsType<OpenAIChatLanguageModel>(OpenAIProvider.Compatible("ollama", "http://localhost:11434/v1").LanguageModel("llama3.2"));
    }
}

public class OpenAIMediaTests
{
    private static (OpenAIProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "sk-test", HttpClient = new HttpClient(handler) }), handler);
    }

    [Fact]
    public async Task GeneratesImagesInBatches()
    {
        var (openai, handler) = Create();
        var png = Convert.ToBase64String([137, 80, 78, 71]);
        handler.Json($$"""{"data":[{"b64_json":"{{png}}","revised_prompt":"un gato"}]}""")
               .Json($$"""{"data":[{"b64_json":"{{png}}"}]}""");

        var result = await Ai.GenerateImageAsync(new GenerateImageOptions
        {
            Model = openai.Image("dall-e-3"),
            Prompt = "Un gato astronauta",
            N = 2,
            Size = "1024x1024",
        });

        Assert.Equal(2, result.Images.Count);
        Assert.Equal("image/png", result.Image.MediaType);
        Assert.Equal([137, 80, 78, 71], result.Image.Data);
        Assert.Equal(2, handler.Requests.Count); // dall-e-3: una imagen por llamada
        var body = handler.Requests[0].Body;
        Assert.Equal("b64_json", (string?)body["response_format"]);
        Assert.Equal(1, (int?)body["n"]);
        Assert.Equal("1024x1024", (string?)body["size"]);
    }

    [Fact]
    public async Task GeneratesSpeech()
    {
        var (openai, handler) = Create();
        handler.Bytes([1, 2, 3, 4], "audio/mpeg");

        var result = await Ai.GenerateSpeechAsync(new GenerateSpeechOptions
        {
            Model = openai.Speech("gpt-4o-mini-tts"),
            Text = "Hola desde .NET",
            Voice = "nova",
            Instructions = "Tono alegre",
        });

        Assert.Equal([1, 2, 3, 4], result.Audio.Data);
        Assert.Equal("audio/mpeg", result.Audio.MediaType);
        var body = handler.Requests[0].Body;
        Assert.Equal("Hola desde .NET", (string?)body["input"]);
        Assert.Equal("nova", (string?)body["voice"]);
        Assert.Equal("Tono alegre", (string?)body["instructions"]);
        Assert.EndsWith("/audio/speech", handler.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task TranscribesAudioWithSegments()
    {
        var (openai, handler) = Create();
        handler.Json("""{"text":"hola mundo","language":"spanish","duration":2.5,"segments":[{"text":" hola","start":0,"end":1.2},{"text":" mundo","start":1.2,"end":2.5}]}""");

        var result = await Ai.TranscribeAsync(new TranscribeOptions
        {
            Model = openai.Transcription("whisper-1"),
            Audio = [9, 9, 9],
            MediaType = "audio/mpeg",
            Language = "es",
        });

        Assert.Equal("hola mundo", result.Text);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("hola", result.Segments[0].Text);
        Assert.Equal(2.5, result.DurationSeconds);
        var raw = handler.Requests[0].Raw;
        Assert.Contains("name=model", raw);
        Assert.Contains("whisper-1", raw);
        Assert.Contains("verbose_json", raw);
        Assert.Contains("filename=audio.mp3", raw);
        Assert.Equal("multipart/form-data", handler.Requests[0].Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ProvidersWithoutMediaSupportThrowNotSupported()
    {
        IProvider anthropic = new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "k" });
        Assert.Throws<NotSupportedException>(() => anthropic.ImageModel("x"));
        var registry = new ProviderRegistry().Register(new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k" }));
        Assert.IsType<OpenAISpeechModel>(registry.SpeechModel("openai:tts-1"));
        await Task.CompletedTask;
    }
}

public class AnthropicServerToolTests
{
    private static (AnthropicProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "sk-ant-test", HttpClient = new HttpClient(handler) }), handler);
    }

    private const string SearchResponse = """
        {"id":"msg_1","model":"claude-opus-5-5","stop_reason":"end_turn",
         "content":[
           {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"clima Santiago"}},
           {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
              {"type":"web_search_result","url":"https://meteo.cl","title":"Meteo","encrypted_content":"abc"},
              {"type":"web_search_result","url":"https://tiempo.cl","title":"Tiempo","encrypted_content":"def"}]},
           {"type":"text","text":"Hay 20 °C","citations":[{"type":"web_search_result_location","url":"https://meteo.cl","title":"Meteo","cited_text":"20 grados"}]},
           {"type":"future_block","data":"x"}
         ],
         "usage":{"input_tokens":10,"output_tokens":20}}
        """;

    [Fact]
    public async Task SendsServerToolsAndParsesResultsAndSources()
    {
        var (anthropic, handler) = Create();
        handler.Json(SearchResponse);

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = anthropic.Messages("claude-opus-5-5"),
            Prompt = "¿Clima en Santiago?",
            Tools = [AnthropicTools.WebSearch(maxUses: 3, allowedDomains: ["meteo.cl"], userLocation: ("Santiago", "CL")), OpenAITools.WebSearch()],
        });

        var tool = handler.Requests[0].Body["tools"]!.AsArray().Single()!;
        Assert.Equal("web_search_20260209", (string?)tool["type"]);
        Assert.Equal(3, (int?)tool["max_uses"]);
        Assert.Equal("meteo.cl", (string?)tool["allowed_domains"]![0]);
        Assert.Equal("CL", (string?)tool["user_location"]!["country"]);

        Assert.Equal("Hay 20 °C", result.Text);
        Assert.Equal(["https://meteo.cl", "https://tiempo.cl"], result.Sources.Select(s => s.Url));
        Assert.Empty(result.PendingToolCalls);
        Assert.Single(result.Steps);
        var call = Assert.Single(result.Steps[0].ToolCalls);
        Assert.True(call.ProviderExecuted);
        Assert.Equal("web_search", Assert.Single(result.Steps[0].ToolResults).ToolName);
        Assert.Contains(result.Content, p => p is RawProviderPart);
    }

    [Fact]
    public async Task ReplaysServerToolBlocksAndUnknownBlocksUnchanged()
    {
        var (anthropic, handler) = Create();
        handler.Json(SearchResponse).Json("""{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""");
        var model = anthropic.Messages("claude-opus-5-5");

        var first = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", Tools = [AnthropicTools.WebSearch()] });
        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Messages = [ModelMessage.User("x"), .. first.ResponseMessages, ModelMessage.User("¿y mañana?")],
            Tools = [AnthropicTools.WebSearch()],
        });

        var assistant = handler.Requests[1].Body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal(["server_tool_use", "web_search_tool_result", "text", "future_block"], assistant.Select(b => (string?)b!["type"]));
        Assert.Equal("abc", (string?)assistant[1]!["content"]![0]!["encrypted_content"]);
        Assert.Equal("x", (string?)assistant[3]!["data"]);
    }

    [Fact]
    public async Task ContinuesAutomaticallyAfterPauseTurn()
    {
        var (anthropic, handler) = Create();
        handler.Json("""
                {"stop_reason":"pause_turn","content":[{"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"q"}}],
                 "usage":{"input_tokens":5,"output_tokens":5}}
                """)
            .Json("""{"stop_reason":"end_turn","content":[{"type":"text","text":"listo"}],"usage":{"input_tokens":7,"output_tokens":3}}""");

        var result = await anthropic.Messages("claude-opus-5-5").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.User("busca")],
            Tools = [AnthropicTools.WebSearch().ToDefinition()],
        });

        Assert.Equal(2, handler.Requests.Count);
        var resent = handler.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal("assistant", (string?)resent[^1]!["role"]);
        Assert.Equal("server_tool_use", (string?)resent[^1]!["content"]![0]!["type"]);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.IsType<ToolCallPart>(result.Content[0]);
        Assert.Equal("listo", Assert.IsType<TextPart>(result.Content[1]).Text);
    }

    [Fact]
    public async Task StreamsServerToolsCitationsAndPauseTurn()
    {
        var (anthropic, handler) = Create();
        static string E(string type, string json) => $"event: {type}\ndata: {json}";
        handler.Sse(
                E("message_start", """{"type":"message_start","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":4,"output_tokens":1}}}"""),
                E("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"s1","name":"web_search","input":{}}}"""),
                E("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"q\"}"}}"""),
                E("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                E("message_delta", """{"type":"message_delta","delta":{"stop_reason":"pause_turn"},"usage":{"output_tokens":6}}"""),
                E("message_stop", """{"type":"message_stop"}"""))
            .Sse(
                E("message_start", """{"type":"message_start","message":{"id":"m2","usage":{"input_tokens":8,"output_tokens":1}}}"""),
                E("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"web_search_tool_result","tool_use_id":"s1","content":[{"type":"web_search_result","url":"https://a.cl","title":"A"}]}}"""),
                E("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                E("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
                E("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Según A"}}"""),
                E("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://b.cl","title":"B","cited_text":"x"}}}"""),
                E("content_block_stop", """{"type":"content_block_stop","index":1}"""),
                E("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":10}}"""),
                E("message_stop", """{"type":"message_stop"}"""));

        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = anthropic.Messages("claude-opus-5-5"),
            Prompt = "x",
            Tools = [AnthropicTools.WebSearch()],
        });
        var result = await stream.Result;

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("server_tool_use", (string?)handler.Requests[1].Body["messages"]![1]!["content"]![0]!["type"]);
        Assert.Equal("Según A", result.Text);
        Assert.Equal(["https://a.cl", "https://b.cl"], result.Sources.Select(s => s.Url));
        Assert.True(result.ToolCalls.Single().ProviderExecuted);
        Assert.True(result.ToolResults.Single().ProviderExecuted);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.Equal(16, result.Usage.OutputTokens);
    }

    [Fact]
    public void CodeExecutionAndFetchFactories()
    {
        Assert.Equal("code_execution_20260521", (string?)AnthropicTools.CodeExecution().ProviderDefinition!["type"]);
        var fetch = AnthropicTools.WebFetch(maxUses: 2, citations: true, maxContentTokens: 5000).ProviderDefinition!;
        Assert.Equal("web_fetch_20260209", (string?)fetch["type"]);
        Assert.True((bool?)fetch["citations"]!["enabled"]);
        Assert.Throws<ArgumentException>(() => AnthropicTools.WebSearch(allowedDomains: ["a"], blockedDomains: ["b"]));
    }
}
