using System.Text;
using System.Text.Json.Nodes;
using AiSdk.Bedrock;
using AiSdk.Cohere;
using AiSdk.Internal;

namespace AiSdk.Tests;

public class AwsTests
{
    [Fact]
    public void SigV4MatchesAwsTestSuiteGetVanilla()
    {
        // Caso "get-vanilla" de la suite oficial de pruebas de AWS Signature Version 4.
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        AwsSigV4.Sign(
            request,
            [],
            "us-east-1",
            "service",
            new AwsCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"),
            new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            request.Headers.GetValues("Authorization").Single());
        Assert.Equal("20150830T123600Z", request.Headers.GetValues("x-amz-date").Single());
    }

    [Fact]
    public void Crc32MatchesStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, AwsEventStream.Crc32(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public async Task EventStreamRoundTripsAndDetectsCorruption()
    {
        var a = AwsEventStream.Encode(new Dictionary<string, string> { [":event-type"] = "contentBlockDelta", [":message-type"] = "event" }, Encoding.UTF8.GetBytes("""{"x":1}"""));
        var b = AwsEventStream.Encode(new Dictionary<string, string> { [":event-type"] = "messageStop" }, Encoding.UTF8.GetBytes("{}"));

        var messages = new List<AwsEventStreamMessage>();
        await foreach (var m in AwsEventStream.ReadAsync(new MemoryStream([.. a, .. b])))
        {
            messages.Add(m);
        }

        Assert.Equal(2, messages.Count);
        Assert.Equal("contentBlockDelta", messages[0].EventType);
        Assert.Equal("""{"x":1}""", messages[0].PayloadText);

        a[^6] ^= 0xFF; // corrompe la carga útil
        await Assert.ThrowsAsync<AiSdkException>(async () =>
        {
            await foreach (var _ in AwsEventStream.ReadAsync(new MemoryStream(a)))
            {
            }
        });
    }
}

public class BedrockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static (BedrockProvider, FakeHandler) Create(string? apiKey = null)
    {
        var handler = new FakeHandler();
        return (new BedrockProvider(new BedrockProviderOptions
        {
            Region = "us-east-1",
            ApiKey = apiKey,
            Credentials = new AwsCredentials("AKID", "SECRET", "TOKEN"),
            HttpClient = new HttpClient(handler),
            Clock = () => Now,
        }), handler);
    }

    private static byte[] Event(string type, string json) =>
        AwsEventStream.Encode(new Dictionary<string, string> { [":event-type"] = type, [":message-type"] = "event", [":content-type"] = "application/json" }, Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task ConverseMapsRequestSignsAndParsesResponse()
    {
        var (bedrock, handler) = Create();
        handler.Json("""
            {"output":{"message":{"role":"assistant","content":[
              {"reasoningContent":{"reasoningText":{"text":"Pienso","signature":"sig"}}},
              {"text":"Consulto"},
              {"toolUse":{"toolUseId":"tu_1","name":"get_weather","input":{"city":"Lima"}}}]}},
             "stopReason":"tool_use","usage":{"inputTokens":10,"outputTokens":5,"cacheReadInputTokens":2}}
            """);

        var result = await bedrock.Converse("us.anthropic.claude-sonnet-4-5-v1:0").GenerateAsync(new LanguageModelRequest
        {
            Messages =
            [
                ModelMessage.System("Eres útil"),
                ModelMessage.User(new TextPart("Clima"), ImagePart.FromBytes([1, 2, 3], "image/jpeg"), new FilePart { Data = [4], MediaType = "application/pdf", Filename = "informe_2026.pdf" }),
            ],
            Tools = [new ToolDefinition("get_weather", "clima", JsonSchema.For<WeatherInput>())],
            ToolChoice = ToolChoice.Required,
            MaxOutputTokens = 300,
            Temperature = 0.2,
        });

        var (request, body) = handler.Requests[0];
        Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com/model/us.anthropic.claude-sonnet-4-5-v1%3A0/converse", request.RequestUri!.AbsoluteUri);
        var auth = request.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/20261016/us-east-1/bedrock/aws4_request, SignedHeaders=host;x-amz-date;x-amz-security-token", auth);
        Assert.Equal("TOKEN", request.Headers.GetValues("x-amz-security-token").Single());
        Assert.Equal("Eres útil", (string?)body["system"]![0]!["text"]);
        Assert.Equal(300, (int?)body["inferenceConfig"]!["maxTokens"]);
        var userContent = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("jpeg", (string?)userContent[1]!["image"]!["format"]);
        Assert.Equal("pdf", (string?)userContent[2]!["document"]!["format"]);
        Assert.Equal("informe 2026", (string?)userContent[2]!["document"]!["name"]);
        Assert.NotNull(body["toolConfig"]!["toolChoice"]!["any"]);
        Assert.Equal("get_weather", (string?)body["toolConfig"]!["tools"]![0]!["toolSpec"]!["name"]);

        Assert.Equal("Pienso", Assert.IsType<ReasoningPart>(result.Content[0]).Text);
        Assert.Equal("Lima", Assert.IsType<ToolCallPart>(result.Content[2]).Input.GetProperty("city").GetString());
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(2, result.Usage.CachedInputTokens);
    }

    [Fact]
    public async Task ApiKeyUsesBearerAndToolLoopReplaysResults()
    {
        var (bedrock, handler) = Create(apiKey: "bedrock-key");
        handler.Json("""{"output":{"message":{"role":"assistant","content":[{"reasoningContent":{"reasoningText":{"text":"r","signature":"s1"}}},{"toolUse":{"toolUseId":"tu_1","name":"get_weather","input":{"city":"Lima"}}}]}},"stopReason":"tool_use","usage":{"inputTokens":1,"outputTokens":1}}""")
            .Json("""{"output":{"message":{"role":"assistant","content":[{"text":"20°C"}]}},"stopReason":"end_turn","usage":{"inputTokens":1,"outputTokens":1}}""");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = bedrock.Converse("amazon.nova-pro-v1:0"),
            Prompt = "Clima",
            Tools = [Tool.Create<WeatherInput, object>("get_weather", "clima", _ => new { temperature = 20 })],
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.Equal("20°C", result.Text);
        Assert.Equal("Bearer bedrock-key", handler.Requests[0].Request.Headers.GetValues("Authorization").Single());
        var messages = handler.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal("s1", (string?)messages[1]!["content"]![0]!["reasoningContent"]!["reasoningText"]!["signature"]);
        var toolResult = messages[2]!["content"]![0]!["toolResult"]!;
        Assert.Equal("tu_1", (string?)toolResult["toolUseId"]);
        Assert.Equal(20, (int?)toolResult["content"]![0]!["json"]!["temperature"]);
    }

    [Fact]
    public async Task ConverseStreamParsesEventStream()
    {
        var (bedrock, handler) = Create();
        handler.Bytes(
        [
            .. Event("messageStart", """{"role":"assistant"}"""),
            .. Event("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"text":"Pienso"}}}"""),
            .. Event("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"signature":"sig-x"}}}"""),
            .. Event("contentBlockStop", """{"contentBlockIndex":0}"""),
            .. Event("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"text":"Hola "}}"""),
            .. Event("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"text":"mundo"}}"""),
            .. Event("contentBlockStop", """{"contentBlockIndex":1}"""),
            .. Event("contentBlockStart", """{"contentBlockIndex":2,"start":{"toolUse":{"toolUseId":"tu_9","name":"get_weather"}}}"""),
            .. Event("contentBlockDelta", """{"contentBlockIndex":2,"delta":{"toolUse":{"input":"{\"city\":"}}}"""),
            .. Event("contentBlockDelta", """{"contentBlockIndex":2,"delta":{"toolUse":{"input":"\"Quito\"}"}}}"""),
            .. Event("contentBlockStop", """{"contentBlockIndex":2}"""),
            .. Event("messageStop", """{"stopReason":"tool_use"}"""),
            .. Event("metadata", """{"usage":{"inputTokens":8,"outputTokens":12},"metrics":{"latencyMs":100}}"""),
        ], "application/vnd.amazon.eventstream");

        var parts = new List<StreamPart>();
        await foreach (var p in bedrock.Converse("anthropic.claude-3-haiku").StreamAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }))
        {
            parts.Add(p);
        }

        var accumulator = new StreamAccumulator();
        parts.ForEach(accumulator.Add);
        var result = accumulator.Build();

        Assert.EndsWith("/converse-stream", handler.Requests[0].Request.RequestUri!.ToString());
        var reasoning = Assert.IsType<ReasoningPart>(result.Content[0]);
        Assert.Equal("sig-x", reasoning.ProviderMetadata![BedrockConverseLanguageModel.SignatureKey].GetString());
        Assert.Equal("Hola mundo", Assert.IsType<TextPart>(result.Content[1]).Text);
        Assert.Equal("Quito", Assert.IsType<ToolCallPart>(result.Content[2]).Input.GetProperty("city").GetString());
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(12, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task StreamExceptionsBecomeErrors()
    {
        var (bedrock, handler) = Create();
        handler.Bytes(
            AwsEventStream.Encode(
                new Dictionary<string, string> { [":message-type"] = "exception", [":exception-type"] = "throttlingException" },
                Encoding.UTF8.GetBytes("""{"message":"Too many requests"}""")),
            "application/vnd.amazon.eventstream");

        var stream = Ai.StreamText(new GenerateTextOptions { Model = bedrock.Converse("m"), Prompt = "x", MaxRetries = 0 });

        var ex = await Assert.ThrowsAsync<ApiCallException>(() => stream.Text);
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("Too many requests", ex.Message);
    }

    [Fact]
    public async Task StructuredOutputViaInstructionAndTitanEmbeddings()
    {
        var (bedrock, handler) = Create();
        handler.Json("""{"output":{"message":{"role":"assistant","content":[{"text":"{\"city\":\"Cusco\"}"}]}},"stopReason":"end_turn","usage":{"inputTokens":1,"outputTokens":1}}""")
            .Json("""{"embedding":[0.1,0.2],"inputTextTokenCount":3}""")
            .Json("""{"embedding":[0.3,0.4],"inputTextTokenCount":4}""");

        var obj = await Ai.GenerateObjectAsync<WeatherInput>(new GenerateObjectOptions { Model = bedrock.Converse("amazon.nova-lite-v1:0"), Prompt = "x" });
        var embeddings = await Ai.EmbedManyAsync(bedrock.Embedding(dimensions: 256), ["a", "b"]);

        Assert.Equal("Cusco", obj.Object.City);
        Assert.Contains("JSON Schema", (string?)handler.Requests[0].Body["system"]![0]!["text"]);
        Assert.Equal(2, embeddings.Embeddings.Count);
        Assert.Equal(7, embeddings.Usage.Tokens);
        Assert.Equal(256, (int?)handler.Requests[1].Body["dimensions"]);
        Assert.Contains("/model/amazon.titan-embed-text-v2%3A0/invoke", handler.Requests[1].Request.RequestUri!.AbsoluteUri);
    }
}

public class CohereTests
{
    [Fact]
    public async Task ReranksAndEmbeds()
    {
        var handler = new FakeHandler()
            .Json("""{"results":[{"index":2,"relevance_score":0.98},{"index":0,"relevance_score":0.4}]}""")
            .Json("""{"embeddings":{"float":[[0.1,0.2],[0.3,0.4]]},"meta":{"billed_units":{"input_tokens":6}}}""");
        var cohere = new CohereProvider(new CohereProviderOptions { ApiKey = "co-key", HttpClient = new HttpClient(handler) });

        var reranked = await Ai.RerankAsync(cohere.Reranking(), "¿capital de Chile?", ["Lima es la capital de Perú", "Buenos Aires", "Santiago es la capital de Chile"], topN: 2);
        var embedded = await Ai.EmbedManyAsync(cohere.Embedding(inputType: "search_query"), ["a", "b"]);

        Assert.Equal("Santiago es la capital de Chile", reranked.RerankedDocuments[0]);
        Assert.Equal(0.98, reranked.Ranking[0].Score);
        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api.cohere.com/v2/rerank", request.RequestUri!.ToString());
        Assert.Equal("Bearer co-key", request.Headers.Authorization!.ToString());
        Assert.Equal(2, (int?)body["top_n"]);
        Assert.Equal([0.3f, 0.4f], embedded.Embeddings[1]);
        Assert.Equal("search_query", (string?)handler.Requests[1].Body["input_type"]);
        Assert.Equal(6, embedded.Usage.Tokens);
    }
}
