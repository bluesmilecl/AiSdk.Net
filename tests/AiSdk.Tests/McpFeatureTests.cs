using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using AiSdk.Mcp;

namespace AiSdk.Tests;

/// <summary>Servidor MCP falso programable: responde con un handler y puede hacer peticiones al cliente.</summary>
internal sealed class ScriptedMcpServer : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly StreamWriter _writer;
    private readonly Task _loop;
    private readonly Func<string, JsonObject?, JsonNode?> _handler;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private int _nextId;

    public ScriptedMcpServer(Func<string, JsonObject?, JsonNode?> handler)
    {
        _handler = handler;
        _writer = new StreamWriter(_serverToClient.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        _loop = Task.Run(LoopAsync);
    }

    public JsonObject? InitializeParams { get; private set; }

    public ConcurrentQueue<string> Notifications { get; } = new();

    public IMcpTransport CreateTransport() =>
        StdioMcpTransport.FromStreams(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream());

    /// <summary>El servidor envía una petición al cliente y espera su respuesta.</summary>
    public async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
    {
        var id = $"srv-{Interlocked.Increment(ref _nextId)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task WriteAsync(JsonObject message)
    {
        var line = message.ToJsonString();
        lock (_writer)
        {
            _writer.WriteLine(line);
        }

        await Task.CompletedTask;
    }

    private async Task LoopAsync()
    {
        using var reader = new StreamReader(_clientToServer.Reader.AsStream(), Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            var msg = JsonNode.Parse(line)!.AsObject();
            var method = (string?)msg["method"];
            if (method is null)
            {
                if (_pending.TryRemove((string)msg["id"]!, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }

                continue;
            }

            if (msg["id"] is null)
            {
                Notifications.Enqueue(method);
                continue;
            }

            if (method == "initialize")
            {
                InitializeParams = msg["params"]!.AsObject();
            }

            var result = method == "initialize"
                ? new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "scripted" } }
                : _handler(method, msg["params"] as JsonObject);
            await WriteAsync(result is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no" } }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["result"] = result });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _serverToClient.Writer.CompleteAsync();
        await _clientToServer.Writer.CompleteAsync();
        await _loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class McpFeatureTests
{
    private static JsonNode? Handler(string method, JsonObject? p) => method switch
    {
        "resources/list" when p?["cursor"] is null => JsonNode.Parse("""{"resources":[{"uri":"file:///readme.md","name":"readme","mimeType":"text/markdown"}],"nextCursor":"2"}"""),
        "resources/list" => JsonNode.Parse("""{"resources":[{"uri":"file:///logo.png","name":"logo","mimeType":"image/png"}]}"""),
        "resources/templates/list" => JsonNode.Parse("""{"resourceTemplates":[{"uriTemplate":"file:///{path}","name":"archivo"}]}"""),
        "resources/read" when (string?)p!["uri"] == "file:///readme.md" => JsonNode.Parse("""{"contents":[{"uri":"file:///readme.md","mimeType":"text/markdown","text":"# Hola"}]}"""),
        "resources/read" => JsonNode.Parse("""{"contents":[{"uri":"file:///logo.png","mimeType":"image/png","blob":"AQID"}]}"""),
        "prompts/list" => JsonNode.Parse("""{"prompts":[{"name":"review","description":"Revisa código","arguments":[{"name":"code","required":true}]}]}"""),
        "prompts/get" => new JsonObject
        {
            ["description"] = "Revisión",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Revisa: " + (string?)p!["arguments"]!["code"] } },
                new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Claro." } }),
        },
        _ => null,
    };

    [Fact]
    public async Task ListsAndReadsResources()
    {
        await using var server = new ScriptedMcpServer(Handler);
        await using var client = await McpClient.ConnectAsync(server.CreateTransport(), cancellationToken: TestContext.Current.CancellationToken);

        var resources = await client.ListResourcesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["readme", "logo"], resources.Select(r => r.Name));
        Assert.Equal("file:///{path}", (await client.ListResourceTemplatesAsync(TestContext.Current.CancellationToken)).Single().UriTemplate);

        var text = await client.ReadResourceAsync("file:///readme.md", TestContext.Current.CancellationToken);
        Assert.Equal("# Hola", text[0].Text);

        var parts = await client.ReadResourceAsPartsAsync("file:///logo.png", TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], Assert.IsType<ImagePart>(parts[0]).Data);
    }

    [Fact]
    public async Task GetsPromptsAsModelMessages()
    {
        await using var server = new ScriptedMcpServer(Handler);
        await using var client = await McpClient.ConnectAsync(server.CreateTransport(), cancellationToken: TestContext.Current.CancellationToken);

        var prompt = Assert.Single(await client.ListPromptsAsync(TestContext.Current.CancellationToken));
        Assert.True(prompt.Arguments[0].Required);

        var result = await client.GetPromptAsync("review", new Dictionary<string, string> { ["code"] = "x = 1" }, TestContext.Current.CancellationToken);
        Assert.Equal("Revisa: x = 1", result.Messages[0].Text);
        Assert.Equal(ChatRole.Assistant, result.Messages[1].Role);
    }

    [Fact]
    public async Task AnswersRootsSamplingAndElicitationRequests()
    {
        await using var server = new ScriptedMcpServer(Handler);
        var samplingModel = new MockLanguageModel("sampler").ReturnsText("resumen generado");
        await using var client = await McpClient.ConnectAsync(server.CreateTransport(), new McpClientOptions
        {
            Roots = [new McpRoot("file:///proyecto", "proyecto")],
            SamplingModel = samplingModel,
            ApproveSampling = (request, _) => Task.FromResult<McpSamplingRequest?>(request), // el usuario aprueba
            ElicitationHandler = (req, _) => Task.FromResult(McpElicitationResult.Accept(new JsonObject { ["nombre"] = "Ana" })),
        }, TestContext.Current.CancellationToken);

        var capabilities = server.InitializeParams!["capabilities"]!;
        Assert.NotNull(capabilities["roots"]);
        Assert.NotNull(capabilities["sampling"]);
        Assert.NotNull(capabilities["elicitation"]);

        var roots = await server.RequestAsync("roots/list", []);
        Assert.Equal("file:///proyecto", (string?)roots["result"]!["roots"]![0]!["uri"]);

        var sampling = await server.RequestAsync("sampling/createMessage", JsonNode.Parse("""
            {"messages":[{"role":"user","content":{"type":"text","text":"Resume esto"}}],"systemPrompt":"Sé breve","maxTokens":100}
            """)!.AsObject());
        Assert.Equal("resumen generado", (string?)sampling["result"]!["content"]!["text"]);
        Assert.Equal("Sé breve", samplingModel.Requests[0].Messages[0].Text);
        Assert.Equal(100, samplingModel.Requests[0].MaxOutputTokens);

        var elicitation = await server.RequestAsync("elicitation/create", JsonNode.Parse("""
            {"message":"¿Tu nombre?","requestedSchema":{"type":"object","properties":{"nombre":{"type":"string"}}}}
            """)!.AsObject());
        Assert.Equal("accept", (string?)elicitation["result"]!["action"]);
        Assert.Equal("Ana", (string?)elicitation["result"]!["content"]!["nombre"]);

        await client.SetRootsAsync([new McpRoot("file:///otro")], TestContext.Current.CancellationToken);
        // La notificación no tiene respuesta: se espera con un sondeo acotado en vez de un retardo fijo.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!server.Notifications.Contains("notifications/roots/list_changed") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Contains("notifications/roots/list_changed", server.Notifications);
    }

    [Fact]
    public async Task UnsupportedServerRequestsGetMethodNotFound()
    {
        await using var server = new ScriptedMcpServer(Handler);
        await using var client = await McpClient.ConnectAsync(server.CreateTransport(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(server.InitializeParams!["capabilities"]!.AsObject());
        var response = await server.RequestAsync("sampling/createMessage", []);
        Assert.Equal(-32601, (int?)response["error"]!["code"]);
    }
}
