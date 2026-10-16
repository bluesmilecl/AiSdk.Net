using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Anthropic;
using AiSdk.OpenAI;
using AiSdk.UI;

namespace AiSdk.Tests;

public class OpenAIProviderTests
{
    private static (OpenAIProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "sk-test", HttpClient = new HttpClient(handler) }), handler);
    }

    [Fact]
    public async Task MapsRequestAndParsesToolCalls()
    {
        var (openai, handler) = Create();
        handler.Json("""
            {"id":"chatcmpl-1","model":"gpt-5","created":1700000000,
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
               "tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Lima\"}"}}]}}],
             "usage":{"prompt_tokens":12,"completion_tokens":7,"completion_tokens_details":{"reasoning_tokens":3}}}
            """);

        var result = await openai.Chat("gpt-5").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.System("sys"), ModelMessage.User("hola")],
            Tools = [new ToolDefinition("get_weather", "clima", JsonSchema.For<WeatherInput>())],
            ToolChoice = ToolChoice.Tool("get_weather"),
            MaxOutputTokens = 100,
            Temperature = 0.5,
            ProviderOptions = new ProviderOptions { ["openai"] = new JsonObject { ["reasoning_effort"] = "low" } },
        });

        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString());
        Assert.Equal("gpt-5", (string?)body["model"]);
        Assert.Equal(100, (int?)body["max_completion_tokens"]);
        Assert.Equal("low", (string?)body["reasoning_effort"]);
        Assert.Equal("system", (string?)body["messages"]![0]!["role"]);
        Assert.Equal("get_weather", (string?)body["tool_choice"]!["function"]!["name"]);

        var call = Assert.IsType<ToolCallPart>(Assert.Single(result.Content));
        Assert.Equal("Lima", call.Input.GetProperty("city").GetString());
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(3, result.Usage.ReasoningTokens);
    }

    [Fact]
    public async Task SerializesToolHistoryAndJsonSchemaFormat()
    {
        var (openai, handler) = Create();
        handler.Json("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"city\":\"Lima\"}"}}]}""");

        await openai.Chat("gpt-5").GenerateAsync(new LanguageModelRequest
        {
            Messages =
            [
                ModelMessage.User("x"),
                ModelMessage.Assistant(new ToolCallPart("c1", "t", JsonDocument.Parse("""{"a":1}""").RootElement)),
                ModelMessage.Tool(new ToolResultPart("c1", "t", JsonDocument.Parse("""{"ok":true}""").RootElement)),
            ],
            ResponseFormat = ResponseFormat.Json(JsonSchema.For<WeatherInput>(), "weather"),
        });

        var body = handler.Requests[0].Body;
        var messages = body["messages"]!.AsArray();
        Assert.Equal("{\"a\":1}", (string?)messages[1]!["tool_calls"]![0]!["function"]!["arguments"]);
        Assert.Equal("tool", (string?)messages[2]!["role"]);
        Assert.Equal("c1", (string?)messages[2]!["tool_call_id"]);
        Assert.Equal("json_schema", (string?)body["response_format"]!["type"]);
        Assert.True((bool?)body["response_format"]!["json_schema"]!["strict"]);
        Assert.False((bool?)body["response_format"]!["json_schema"]!["schema"]!["additionalProperties"]);
    }

    [Fact]
    public async Task StreamsTextAndToolCalls()
    {
        var (openai, handler) = Create();
        handler.Sse(
            """data: {"id":"c1","model":"gpt-5","choices":[{"delta":{"role":"assistant","content":"Hola"}}]}""",
            """data: {"id":"c1","choices":[{"delta":{"content":" mundo"}}]}""",
            """data: {"id":"c1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","function":{"name":"get_weather","arguments":"{\"ci"}}]}}]}""",
            """data: {"id":"c1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"Lima\"}"}}]}}]}""",
            """data: {"id":"c1","choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            """data: {"id":"c1","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":9}}""",
            "data: [DONE]");

        var parts = new List<StreamPart>();
        await foreach (var p in openai.Chat("gpt-5").StreamAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }))
        {
            parts.Add(p);
        }

        Assert.True((bool?)handler.Requests[0].Body["stream_options"]!["include_usage"]);
        Assert.Equal("Hola mundo", string.Concat(parts.OfType<TextDeltaPart>().Select(t => t.Delta)));
        var call = Assert.Single(parts.OfType<ToolCallStreamPart>()).ToolCall;
        Assert.Equal("call_9", call.ToolCallId);
        Assert.Equal("Lima", call.Input.GetProperty("city").GetString());
        var finish = Assert.IsType<ModelFinishPart>(parts[^1]);
        Assert.Equal(FinishReason.ToolCalls, finish.FinishReason);
        Assert.Equal(9, finish.Usage.OutputTokens);
    }

    [Fact]
    public async Task ThrowsApiCallExceptionWithStatus()
    {
        var (openai, handler) = Create();
        handler.Json("""{"error":{"message":"Invalid model"}}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<ApiCallException>(() =>
            openai.Chat("nope").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Invalid model", ex.Message);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task EmbedsValues()
    {
        var (openai, handler) = Create();
        handler.Json("""{"data":[{"index":1,"embedding":[0.0,1.0]},{"index":0,"embedding":[1.0,0.0]}],"usage":{"prompt_tokens":4}}""");

        var result = await Ai.EmbedManyAsync(openai.Embedding("text-embedding-3-small"), ["a", "b"]);

        Assert.Equal([1f, 0f], result.Embeddings[0]);
        Assert.Equal(4, result.Usage.Tokens);
    }
}

public class AnthropicProviderTests
{
    private static (AnthropicProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "sk-ant-test", HttpClient = new HttpClient(handler) }), handler);
    }

    [Fact]
    public async Task MapsMessagesToolsAndThinking()
    {
        var (anthropic, handler) = Create();
        handler.Json("""
            {"id":"msg_1","model":"claude-opus-5-5","stop_reason":"tool_use",
             "content":[
               {"type":"thinking","thinking":"","signature":"sig-abc"},
               {"type":"text","text":"Voy a consultar."},
               {"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Lima"}}],
             "usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":5}}
            """);

        var result = await anthropic.Messages("claude-opus-5-5").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.System("Eres útil"), ModelMessage.User("Clima en Lima")],
            Tools = [new ToolDefinition("get_weather", "clima", JsonSchema.For<WeatherInput>())],
            ProviderOptions = AnthropicOptions.Create(effort: "high", thinkingDisplay: "summarized", refusalFallback: true),
        });

        var (request, body) = handler.Requests[0];
        Assert.Equal("sk-ant-test", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("server-side-fallback-2026-07-01", request.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("Eres útil", (string?)body["system"]);
        Assert.Equal(16000, (int?)body["max_tokens"]);
        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
        Assert.Equal("adaptive", (string?)body["thinking"]!["type"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Null(body["betas"]);
        Assert.Equal("get_weather", (string?)body["tools"]![0]!["name"]);
        Assert.Null(body["tools"]![0]!["eager_input_streaming"]);

        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(15, result.Usage.InputTokens);
        Assert.Equal(5, result.Usage.CachedInputTokens);
        var reasoning = Assert.IsType<ReasoningPart>(result.Content[0]);
        Assert.Equal("sig-abc", reasoning.ProviderMetadata![AnthropicMessagesLanguageModel.SignatureKey].GetString());
    }

    [Fact]
    public async Task ReplaysThinkingAndGroupsToolResultsAsUser()
    {
        var (anthropic, handler) = Create();
        handler.Json("""{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""");
        var signature = new Dictionary<string, JsonElement> { [AnthropicMessagesLanguageModel.SignatureKey] = JsonSerializer.SerializeToElement("sig-abc") };

        await anthropic.Messages("claude-opus-5-5").GenerateAsync(new LanguageModelRequest
        {
            Messages =
            [
                ModelMessage.User("x"),
                ModelMessage.Assistant(
                    new ReasoningPart("") { ProviderMetadata = signature },
                    new ToolCallPart("toolu_1", "a", JsonDocument.Parse("{}").RootElement),
                    new ToolCallPart("toolu_2", "b", JsonDocument.Parse("{}").RootElement)),
                ModelMessage.Tool(
                    new ToolResultPart("toolu_1", "a", JsonSerializer.SerializeToElement("r1")),
                    new ToolResultPart("toolu_2", "b", JsonSerializer.SerializeToElement("r2")) { IsError = true }),
                ModelMessage.User("sigue"),
            ],
        });

        var messages = handler.Requests[0].Body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count); // user, assistant, user(tool_results + texto)
        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal("thinking", (string?)assistant[0]!["type"]);
        Assert.Equal("sig-abc", (string?)assistant[0]!["signature"]);
        var user = messages[2]!["content"]!.AsArray();
        Assert.Equal(["tool_result", "tool_result", "text"], user.Select(b => (string?)b!["type"]));
        Assert.True((bool?)user[1]!["is_error"]);
    }

    [Fact]
    public async Task StreamsThinkingTextAndToolUse()
    {
        var (anthropic, handler) = Create();
        handler.Sse(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Pienso...\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-1\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Consulto\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"get_weather\",\"input\":{}}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\"}}",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"Lima\\\"}\"}}",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":2}",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":40}}",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}");

        var parts = new List<StreamPart>();
        await foreach (var p in anthropic.Messages("claude-opus-5-5").StreamAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.User("x")],
            Tools = [new ToolDefinition("get_weather", null, JsonSchema.For<WeatherInput>())],
        }))
        {
            parts.Add(p);
        }

        var body = handler.Requests[0].Body;
        Assert.True((bool?)body["stream"]);
        Assert.Equal(64000, (int?)body["max_tokens"]);
        Assert.True((bool?)body["tools"]![0]!["eager_input_streaming"]);

        var accumulator = new AiSdk.Internal.StreamAccumulator();
        parts.ForEach(accumulator.Add);
        var result = accumulator.Build();
        var reasoning = Assert.IsType<ReasoningPart>(result.Content[0]);
        Assert.Equal("Pienso...", reasoning.Text);
        Assert.Equal("sig-1", reasoning.ProviderMetadata![AnthropicMessagesLanguageModel.SignatureKey].GetString());
        Assert.Equal("Consulto", Assert.IsType<TextPart>(result.Content[1]).Text);
        Assert.Equal("Lima", Assert.IsType<ToolCallPart>(result.Content[2]).Input.GetProperty("city").GetString());
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(40, result.Usage.OutputTokens);
        Assert.Equal(12, result.Usage.InputTokens);
    }

    [Fact]
    public async Task StructuredOutputUsesOutputConfigFormat()
    {
        var (anthropic, handler) = Create();
        handler.Json("""{"content":[{"type":"text","text":"{\"city\":\"Lima\"}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""");

        var result = await Ai.GenerateObjectAsync<WeatherInput>(new GenerateObjectOptions
        {
            Model = anthropic.Messages("claude-opus-5-5"),
            Prompt = "x",
            ProviderOptions = AnthropicOptions.Create(effort: "low"),
        });

        var config = handler.Requests[0].Body["output_config"]!;
        Assert.Equal("json_schema", (string?)config["format"]!["type"]);
        Assert.Equal("low", (string?)config["effort"]);
        Assert.Equal("Lima", result.Object.City);
    }
}

public class UIMessageStreamTests
{
    [Fact]
    public async Task WritesUIMessageStreamProtocol()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("echo", new { city = "x" }, "c1")
            .ReturnsText("fin");
        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Tool.Create<WeatherInput, string>("echo", "eco", i => i.City)],
            StopWhen = StopCondition.StepCountIs(3),
        });

        using var output = new MemoryStream();
        await UIMessageStream.WriteAsync(stream.FullStream, output, messageId: "m1");
        var text = Encoding.UTF8.GetString(output.ToArray());

        var events = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l["data: ".Length..]).ToList();
        Assert.Equal("[DONE]", events[^1]);
        var types = events.SkipLast(1).Select(e => (string?)JsonNode.Parse(e)!["type"]).ToList();
        Assert.Equal("start", types[0]);
        Assert.Equal("finish", types[^1]);
        Assert.Contains("tool-input-available", types);
        Assert.Contains("tool-output-available", types);
        Assert.Contains("text-delta", types);
        Assert.Contains("\"messageId\":\"m1\"", events[0]);
    }

    [Fact]
    public void ConvertsUIMessagesToModelMessages()
    {
        var request = JsonSerializer.Deserialize<UIChatRequest>("""
            {"id":"chat1","messages":[
              {"id":"1","role":"user","parts":[{"type":"text","text":"Clima en Lima"}]},
              {"id":"2","role":"assistant","parts":[
                {"type":"step-start"},
                {"type":"tool-get_weather","toolCallId":"c1","state":"output-available","input":{"city":"Lima"},"output":{"temperatureC":21}},
                {"type":"step-start"},
                {"type":"text","text":"21°C"}]},
              {"id":"3","role":"user","parts":[{"type":"text","text":"gracias"},{"type":"file","mediaType":"image/png","url":"data:image/png;base64,AQID"}]}
            ]}
            """)!;

        var messages = UIMessageConverter.ToModelMessages(request.Messages);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("get_weather", Assert.IsType<ToolCallPart>(messages[1].Content[0]).ToolName);
        Assert.Equal(21, Assert.IsType<ToolResultPart>(messages[2].Content[0]).Output.GetProperty("temperatureC").GetInt32());
        Assert.Equal([1, 2, 3], Assert.IsType<ImagePart>(messages[4].Content[1]).Data);
    }
}

public class AspNetCoreTests
{
    [Fact]
    public async Task UIMessageStreamResultSetsHeadersAndBody()
    {
        var model = new MockLanguageModel().ReturnsText("hola");
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await AiSdk.AspNetCore.StreamResultExtensions.ToUIMessageStreamResult(Ai.StreamText(model, "x")).ExecuteAsync(context);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("v1", context.Response.Headers["x-vercel-ai-ui-message-stream"]);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("\"type\":\"text-delta\"", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }
}
