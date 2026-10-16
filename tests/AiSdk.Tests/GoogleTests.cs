using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Anthropic;
using AiSdk.Google;
using AiSdk.Internal;

namespace AiSdk.Tests;

public class GoogleTests
{
    private static (GoogleProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new GoogleProvider(new GoogleProviderOptions { ApiKey = "g-key", HttpClient = new HttpClient(handler) }), handler);
    }

    private const string ToolCallResponse = """
        {"candidates":[{"content":{"role":"model","parts":[
            {"text":"Reviso el clima","thought":true,"thoughtSignature":"sig-1"},
            {"functionCall":{"name":"get_weather","args":{"city":"Lima"}},"thoughtSignature":"sig-2"}]},
          "finishReason":"STOP"}],
         "usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":5,"thoughtsTokenCount":7,"cachedContentTokenCount":2},
         "modelVersion":"gemini-2.5-flash","responseId":"r1"}
        """;

    [Fact]
    public async Task MapsRequestAndParsesFunctionCallsWithSignatures()
    {
        var (google, handler) = Create();
        handler.Json(ToolCallResponse);

        var result = await google.Gemini("gemini-2.5-flash").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.System("Eres útil"), ModelMessage.User(new TextPart("Clima"), ImagePart.FromBytes([1, 2], "image/jpeg"))],
            Tools = [new ToolDefinition("get_weather", "clima", JsonSchema.For<WeatherInput>()), GoogleTools.GoogleSearch().ToDefinition(), AnthropicTools.WebSearch().ToDefinition()],
            ToolChoice = ToolChoice.Tool("get_weather"),
            MaxOutputTokens = 200,
            Temperature = 0.3,
            ProviderOptions = GoogleOptions.Create(thinkingBudget: 1024, includeThoughts: true),
        });

        var (request, body) = handler.Requests[0];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", request.RequestUri!.ToString());
        Assert.Equal("g-key", request.Headers.GetValues("x-goog-api-key").Single());
        Assert.Equal("Eres útil", (string?)body["systemInstruction"]!["parts"]![0]!["text"]);
        Assert.Equal("image/jpeg", (string?)body["contents"]![0]!["parts"]![1]!["inlineData"]!["mimeType"]);
        Assert.Equal(200, (int?)body["generationConfig"]!["maxOutputTokens"]);
        Assert.Equal(1024, (int?)body["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]);
        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count); // la de Anthropic se omite
        Assert.Equal("get_weather", (string?)tools[0]!["functionDeclarations"]![0]!["name"]);
        Assert.NotNull(tools[0]!["functionDeclarations"]![0]!["parametersJsonSchema"]);
        Assert.NotNull(tools[1]!["googleSearch"]);
        Assert.Equal("ANY", (string?)body["toolConfig"]!["functionCallingConfig"]!["mode"]);
        Assert.Equal("get_weather", (string?)body["toolConfig"]!["functionCallingConfig"]!["allowedFunctionNames"]![0]);

        Assert.Equal("Reviso el clima", Assert.IsType<ReasoningPart>(result.Content[0]).Text);
        var call = Assert.IsType<ToolCallPart>(result.Content[1]);
        Assert.Equal("Lima", call.Input.GetProperty("city").GetString());
        Assert.Equal("sig-2", call.ProviderMetadata![GoogleGenerativeLanguageModel.ThoughtSignatureKey].GetString());
        Assert.Equal(FinishReason.ToolCalls, result.FinishReason);
        Assert.Equal(12, result.Usage.OutputTokens);
        Assert.Equal(7, result.Usage.ReasoningTokens);
        Assert.Contains(result.Warnings, w => w.Contains("anthropic"));
    }

    [Fact]
    public async Task ReplaysSignaturesAndFunctionResponsesInLoop()
    {
        var (google, handler) = Create();
        handler.Json(ToolCallResponse)
            .Json("""{"candidates":[{"content":{"role":"model","parts":[{"text":"En Lima hay 20°C"}]},"finishReason":"STOP"}]}""");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = google.Gemini("gemini-2.5-flash"),
            Prompt = "Clima en Lima",
            Tools = [Tool.Create<WeatherInput, object>("get_weather", "clima", i => new { temperature = 20 })],
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.Equal("En Lima hay 20°C", result.Text);
        var contents = handler.Requests[1].Body["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => (string?)c!["role"]));
        var modelParts = contents[1]!["parts"]!.AsArray();
        Assert.True((bool?)modelParts[0]!["thought"]);
        Assert.Equal("sig-1", (string?)modelParts[0]!["thoughtSignature"]);
        Assert.Equal("sig-2", (string?)modelParts[1]!["thoughtSignature"]);
        var response = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("get_weather", (string?)response["name"]);
        Assert.Equal(20, (int?)response["response"]!["content"]!["temperature"]);
    }

    [Fact]
    public async Task StreamsTextReasoningSourcesAndCodeExecution()
    {
        var (google, handler) = Create();
        handler.Sse(
            """data: {"candidates":[{"content":{"parts":[{"text":"Pienso","thought":true}]}}],"responseId":"r1"}""",
            """data: {"candidates":[{"content":{"parts":[{"executableCode":{"language":"PYTHON","code":"print(2+2)"}}]}}]}""",
            """data: {"candidates":[{"content":{"parts":[{"codeExecutionResult":{"outcome":"OUTCOME_OK","output":"4"}}]}}]}""",
            """data: {"candidates":[{"content":{"parts":[{"text":"Son "}]}}]}""",
            """data: {"candidates":[{"content":{"parts":[{"text":"4"}]},"finishReason":"STOP","groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://a.cl","title":"A"}}]}}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":4}}""");

        var result = await Ai.StreamText(new GenerateTextOptions
        {
            Model = google.Gemini("gemini-2.5-flash"),
            Prompt = "x",
            Tools = [GoogleTools.CodeExecution(), GoogleTools.GoogleSearch()],
        }).Result;

        Assert.Contains(":streamGenerateContent?alt=sse", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal("Pienso", result.ReasoningText);
        Assert.Equal("Son 4", result.Text);
        Assert.Equal("https://a.cl", Assert.Single(result.Sources).Url);
        var code = Assert.Single(result.ToolCalls);
        Assert.True(code.ProviderExecuted);
        Assert.Equal("4", result.ToolResults.Single().Output.GetProperty("output").GetString());
        Assert.Equal(code.ToolCallId, result.ToolResults.Single().ToolCallId);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Single(result.Steps);
    }

    [Fact]
    public async Task StructuredOutputUsesResponseJsonSchema()
    {
        var (google, handler) = Create();
        handler.Json("""{"candidates":[{"content":{"parts":[{"text":"{\"city\":\"Quito\"}"}]},"finishReason":"STOP"}]}""");

        var result = await Ai.GenerateObjectAsync<WeatherInput>(new GenerateObjectOptions { Model = google.Gemini("gemini-2.5-flash"), Prompt = "x" });

        var config = handler.Requests[0].Body["generationConfig"]!;
        Assert.Equal("application/json", (string?)config["responseMimeType"]);
        Assert.Equal("object", (string?)config["responseJsonSchema"]!["type"]);
        Assert.Equal("Quito", result.Object.City);
    }

    [Fact]
    public async Task EmbedsCountsTokensAndGeneratesImages()
    {
        var (google, handler) = Create();
        handler.Json("""{"embeddings":[{"values":[0.1,0.2]},{"values":[0.3,0.4]}]}""")
            .Json("""{"totalTokens":25}""")
            .Json("""{"predictions":[{"bytesBase64Encoded":"AQID","mimeType":"image/png"}]}""");

        var embeddings = await Ai.EmbedManyAsync(google.Embedding(outputDimensionality: 2, taskType: "RETRIEVAL_DOCUMENT"), ["a", "b"]);
        var tokens = await Ai.CountTokensAsync(new GenerateTextOptions { Model = google.Gemini("gemini-2.5-flash"), System = "s", Prompt = "hola" });
        var image = await Ai.GenerateImageAsync(new GenerateImageOptions { Model = google.Image("imagen-4.0-generate-001"), Prompt = "gato", AspectRatio = "16:9" });

        Assert.Equal([0.3f, 0.4f], embeddings.Embeddings[1]);
        var embedRequest = handler.Requests[0].Body["requests"]![0]!;
        Assert.Equal("models/gemini-embedding-001", (string?)embedRequest["model"]);
        Assert.Equal("RETRIEVAL_DOCUMENT", (string?)embedRequest["taskType"]);
        Assert.Equal(25, tokens);
        Assert.EndsWith(":countTokens", handler.Requests[1].Request.RequestUri!.ToString());
        Assert.Equal("models/gemini-2.5-flash", (string?)handler.Requests[1].Body["generateContentRequest"]!["model"]);
        Assert.Equal([1, 2, 3], image.Image.Data);
        Assert.Equal("16:9", (string?)handler.Requests[2].Body["parameters"]!["aspectRatio"]);
    }
}
