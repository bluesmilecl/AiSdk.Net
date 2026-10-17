using System.Text;
using System.Text.Json.Nodes;
using AiSdk.Anthropic;
using AiSdk.Mistral;
using AiSdk.OpenAI;

namespace AiSdk.Tests;

public class AnthropicServiceTests
{
    private static (AnthropicProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "sk-ant-test", HttpClient = new HttpClient(handler) }), handler);
    }

    [Fact]
    public async Task CountsTokens()
    {
        var (anthropic, handler) = Create();
        handler.Json("""{"input_tokens":42}""");

        var tokens = await Ai.CountTokensAsync(new GenerateTextOptions
        {
            Model = anthropic.Messages("claude-opus-5-5"),
            System = "sys",
            Prompt = "hola",
            Tools = [Tool.Create<WeatherInput, string>("w", "clima", _ => "x")],
            MaxOutputTokens = 100,
        });

        Assert.Equal(42, tokens);
        var (request, body) = handler.Requests[0];
        Assert.EndsWith("/v1/messages/count_tokens", request.RequestUri!.ToString());
        Assert.Equal("sys", (string?)body["system"]);
        Assert.NotNull(body["tools"]);
        Assert.Null(body["max_tokens"]);
    }

    [Fact]
    public async Task CreatesBatchAndReadsResults()
    {
        var (anthropic, handler) = Create();
        handler.Json("""{"id":"msgbatch_1","processing_status":"in_progress","request_counts":{"processing":2,"succeeded":0,"errored":0,"canceled":0,"expired":0}}""")
            .Json("""{"id":"msgbatch_1","processing_status":"ended","request_counts":{"processing":0,"succeeded":1,"errored":1,"canceled":0,"expired":0},"ended_at":"2026-10-16T10:00:00Z"}""")
            .Json("""
                {"custom_id":"b","result":{"type":"errored","error":{"type":"error","error":{"type":"invalid_request_error","message":"mal"}}}}
                {"custom_id":"a","result":{"type":"succeeded","message":{"id":"m","content":[{"type":"text","text":"hola A"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}}}
                """);
        var batches = anthropic.Batches("claude-opus-5-5");

        var job = await batches.CreateAsync([
            new BatchRequestItem("a", new LanguageModelRequest { Messages = [ModelMessage.User("A")] }),
            new BatchRequestItem("b", new LanguageModelRequest { Messages = [ModelMessage.User("B")] }),
        ], TestContext.Current.CancellationToken);
        Assert.Equal(BatchStatus.InProgress, job.Status);
        var requests = handler.Requests[0].Body["requests"]!.AsArray();
        Assert.Equal("a", (string?)requests[0]!["custom_id"]);
        Assert.Equal("claude-opus-5-5", (string?)requests[0]!["params"]!["model"]);
        Assert.Null(requests[0]!["params"]!["stream"]);

        var finished = await batches.WaitForCompletionAsync("msgbatch_1", TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchStatus.Completed, finished.Status);
        Assert.Equal(1, finished.Counts.Failed);

        var results = new Dictionary<string, BatchResultItem>();
        await foreach (var r in batches.GetResultsAsync("msgbatch_1", TestContext.Current.CancellationToken))
        {
            results[r.CustomId] = r;
        }

        Assert.Equal("hola A", results["a"].Text);
        Assert.Equal("mal", results["b"].Error);
    }

    [Fact]
    public async Task UploadsFilesAndReferencesThemInMessages()
    {
        var (anthropic, handler) = Create();
        handler.Json("""{"id":"file_abc","filename":"doc.pdf","mime_type":"application/pdf","size_bytes":3,"created_at":"2026-10-16T10:00:00Z"}""")
            .Json("""{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""");

        var file = await anthropic.Files.UploadAsync([1, 2, 3], "doc.pdf", "application/pdf", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("file_abc", file.Id);
        Assert.Contains("filename=doc.pdf", handler.Requests[0].Raw);

        await anthropic.Messages("claude-opus-5-5").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.User(FilePart.FromFileId(file.Id), new TextPart("Resume"))],
        }, TestContext.Current.CancellationToken);

        var source = handler.Requests[1].Body["messages"]![0]!["content"]![0]!["source"]!;
        Assert.Equal("file", (string?)source["type"]);
        Assert.Equal("file_abc", (string?)source["file_id"]);
    }
}

public class OpenAIServiceTests
{
    private static (OpenAIProvider, FakeHandler) Create()
    {
        var handler = new FakeHandler();
        return (new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "sk-test", HttpClient = new HttpClient(handler) }), handler);
    }

    [Fact]
    public async Task CountsTokensWithResponsesApi()
    {
        var (openai, handler) = Create();
        handler.Json("""{"object":"response.input_tokens","input_tokens":17}""");

        var tokens = await Ai.CountTokensAsync(new GenerateTextOptions { Model = openai.Responses("gpt-5"), Prompt = "hola", MaxOutputTokens = 9 });

        Assert.Equal(17, tokens);
        Assert.EndsWith("/responses/input_tokens", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Null(handler.Requests[0].Body["max_output_tokens"]);
    }

    [Fact]
    public async Task BatchUploadsJsonlAndParsesResults()
    {
        var (openai, handler) = Create();
        handler.Json("""{"id":"file-in","filename":"batch.jsonl","bytes":10}""")
            .Json("""{"id":"batch_1","status":"validating","request_counts":{"total":2,"completed":0,"failed":0}}""")
            .Json("""{"id":"batch_1","status":"completed","output_file_id":"file-out","error_file_id":null,"request_counts":{"total":2,"completed":1,"failed":1}}""")
            .Json("""
                {"custom_id":"a","response":{"status_code":200,"body":{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"respuesta A"}]}]}}}
                {"custom_id":"b","response":{"status_code":400,"body":{"error":{"message":"modelo inválido"}}}}
                """);
        var batches = openai.Batches("gpt-5");

        var job = await batches.CreateAsync([new BatchRequestItem("a", new LanguageModelRequest { Messages = [ModelMessage.User("A")] }), new BatchRequestItem("b", new LanguageModelRequest { Messages = [ModelMessage.User("B")] })], TestContext.Current.CancellationToken);

        Assert.Equal(BatchStatus.InProgress, job.Status);
        var upload = handler.Requests[0].Raw;
        Assert.Contains("name=purpose", upload);
        Assert.Contains("batch", upload);
        Assert.Contains("\"custom_id\":\"a\"", upload);
        Assert.Contains("\"url\":\"/v1/responses\"", upload);
        Assert.Equal("file-in", (string?)handler.Requests[1].Body["input_file_id"]);
        Assert.Equal("/v1/responses", (string?)handler.Requests[1].Body["endpoint"]);

        var results = new List<BatchResultItem>();
        await foreach (var r in batches.GetResultsAsync("batch_1", TestContext.Current.CancellationToken))
        {
            results.Add(r);
        }

        Assert.Equal("respuesta A", results.Single(r => r.CustomId == "a").Text);
        Assert.Equal("modelo inválido", results.Single(r => r.CustomId == "b").Error);
        Assert.EndsWith("/files/file-out/content", handler.Requests[3].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task VectorStoresUploadWaitAndSearch()
    {
        var (openai, handler) = Create();
        handler.Json("""{"id":"vs_1","name":"docs","status":"in_progress","file_counts":{"completed":0,"in_progress":0,"failed":0}}""")
            .Json("""{"id":"file-1","filename":"guia.md","bytes":5}""")
            .Json("""{"id":"vsf_1","status":"in_progress"}""")
            .Json("""{"id":"vs_1","name":"docs","status":"in_progress","file_counts":{"completed":0,"in_progress":1,"failed":0}}""")
            .Json("""{"id":"vs_1","name":"docs","status":"completed","file_counts":{"completed":1,"in_progress":0,"failed":0}}""")
            .Json("""{"data":[{"file_id":"file-1","filename":"guia.md","score":0.91,"content":[{"type":"text","text":"Instalación con dotnet add package"}]}]}""");
        var stores = openai.VectorStores;

        var store = await stores.CreateAsync("docs", cancellationToken: TestContext.Current.CancellationToken);
        await stores.UploadFileAsync(store.Id, Encoding.UTF8.GetBytes("# Guía"), "guia.md", "text/markdown", TestContext.Current.CancellationToken);
        var ready = await stores.WaitUntilReadyAsync(store.Id, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var hits = await stores.SearchAsync(store.Id, "¿cómo instalo?", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(ready.IsReady);
        Assert.Contains("assistants", handler.Requests[1].Raw);
        Assert.Equal("file-1", (string?)handler.Requests[2].Body["file_id"]);
        Assert.Equal(0.91, hits[0].Score);
        Assert.StartsWith("Instalación", hits[0].Text);
    }

    [Fact]
    public async Task ReferencesUploadedFilesById()
    {
        var (openai, handler) = Create();
        handler.Json("""{"status":"completed","output":[]}""").Json("""{"choices":[{"finish_reason":"stop","message":{"content":"ok"}}]}""");
        var message = ModelMessage.User(FilePart.FromFileId("file-9"), ImagePart.FromFileId("file-img"));

        await openai.Responses("gpt-5").GenerateAsync(new LanguageModelRequest { Messages = [message] }, TestContext.Current.CancellationToken);
        await openai.Chat("gpt-5").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User(FilePart.FromFileId("file-9"))] }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => openai.Chat("gpt-5").GenerateAsync(new LanguageModelRequest { Messages = [message] }, TestContext.Current.CancellationToken));

        var responsesContent = handler.Requests[0].Body["input"]![0]!["content"]!.AsArray();
        Assert.Equal("file-9", (string?)responsesContent[0]!["file_id"]);
        Assert.Equal("file-img", (string?)responsesContent[1]!["file_id"]);
        Assert.Equal("file-9", (string?)handler.Requests[1].Body["messages"]![0]!["content"]![0]!["file"]!["file_id"]);
    }
}

public class MistralTests
{
    [Fact]
    public async Task UsesMistralDialectAndParsesThinkingBlocks()
    {
        var handler = new FakeHandler().Json("""
            {"id":"x","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":[
              {"type":"thinking","thinking":[{"type":"text","text":"Analizo"}]},
              {"type":"text","text":"Respuesta"}]}}],
             "usage":{"prompt_tokens":3,"completion_tokens":4}}
            """);
        var mistral = new MistralProvider(new MistralProviderOptions { ApiKey = "m-key", HttpClient = new HttpClient(handler) });

        var result = await mistral.Chat("magistral-medium-latest").GenerateAsync(new LanguageModelRequest
        {
            Messages = [ModelMessage.User("hola")],
            Tools = [new ToolDefinition("w", null, JsonSchema.For<WeatherInput>())],
            ToolChoice = ToolChoice.Required,
            MaxOutputTokens = 64,
            Seed = 7,
        }, TestContext.Current.CancellationToken);

        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api.mistral.ai/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer m-key", request.Headers.Authorization!.ToString());
        Assert.Equal("any", (string?)body["tool_choice"]);
        Assert.Equal(64, (int?)body["max_tokens"]);
        Assert.Equal(7, (int?)body["random_seed"]);
        Assert.Null(body["seed"]);
        Assert.Equal("Analizo", Assert.IsType<ReasoningPart>(result.Content[0]).Text);
        Assert.Equal("Respuesta", Assert.IsType<TextPart>(result.Content[1]).Text);
    }

    [Fact]
    public async Task StreamsWithoutStreamOptionsAndHandlesArrayDeltas()
    {
        var handler = new FakeHandler().Sse(
            """data: {"id":"x","choices":[{"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"Pienso"}]}]}}]}""",
            """data: {"id":"x","choices":[{"delta":{"content":"Hola"}}]}""",
            """data: {"id":"x","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}""",
            "data: [DONE]");
        var mistral = new MistralProvider(new MistralProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        var result = await Ai.StreamText(new GenerateTextOptions { Model = mistral.Chat("magistral-small-latest"), Prompt = "x" }).Result;

        Assert.Null(handler.Requests[0].Body["stream_options"]);
        Assert.Equal("Pienso", result.ReasoningText);
        Assert.Equal("Hola", result.Text);
        Assert.Equal(2, result.Usage.OutputTokens);
    }
}
