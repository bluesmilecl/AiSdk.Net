using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Mcp;

namespace AiSdk.Tests;

/// <summary>Servidor MCP falso que habla JSON-RPC por streams (como un proceso stdio).</summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly Task _loop;
    private readonly StreamWriter _writer;
    private readonly TaskCompletionSource<JsonObject> _pingResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeMcpServer()
    {
        _writer = new StreamWriter(_serverToClient.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        _loop = Task.Run(LoopAsync);
    }

    public List<string> ReceivedMethods { get; } = [];

    public Task<JsonObject> PingResponse => _pingResponse.Task;

    public IMcpTransport CreateTransport() =>
        StdioMcpTransport.FromStreams(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream());

    private async Task LoopAsync()
    {
        using var reader = new StreamReader(_clientToServer.Reader.AsStream(), Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            var msg = JsonNode.Parse(line)!.AsObject();
            var method = (string?)msg["method"];
            if (method is null)
            {
                _pingResponse.TrySetResult(msg); // respuesta del cliente a nuestro ping
                continue;
            }

            lock (ReceivedMethods)
            {
                ReceivedMethods.Add(method);
            }

            var id = msg["id"]?.DeepClone();
            JsonNode? result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake-server", ["version"] = "1.2.3" },
                    ["instructions"] = "Usa add para sumar.",
                },
                "tools/list" when msg["params"]?["cursor"] is null => new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "add",
                        ["description"] = "Suma dos números",
                        ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}"""),
                    }),
                    ["nextCursor"] = "page-2",
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject { ["name"] = "fail", ["description"] = "Siempre falla", ["inputSchema"] = new JsonObject { ["type"] = "object" } }),
                },
                "tools/call" when (string?)msg["params"]?["name"] == "add" => Sum(msg),
                "tools/call" => new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "boom" }),
                    ["isError"] = true,
                },
                _ => null,
            };

            if (id is null)
            {
                continue; // notificación
            }

            if ((string?)msg["params"]?["name"] == "add")
            {
                // El servidor hace una petición al cliente antes de responder.
                await _writer.WriteLineAsync("""{"jsonrpc":"2.0","id":"srv-1","method":"ping"}""");
            }

            var response = result is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no" } }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            await _writer.WriteLineAsync(response.ToJsonString());
        }
    }

    private static JsonObject Sum(JsonObject msg)
    {
        var a = (double)msg["params"]!["arguments"]!["a"]!;
        var b = (double)msg["params"]!["arguments"]!["b"]!;
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            ["structuredContent"] = new JsonObject { ["sum"] = a + b },
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _serverToClient.Writer.CompleteAsync();
        await _clientToServer.Writer.CompleteAsync();
        await _loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class McpTests
{
    [Fact]
    public async Task ConnectsListsAndCallsToolsOverStdio()
    {
        await using var server = new FakeMcpServer();
        await using var client = await McpClient.ConnectAsync(server.CreateTransport(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fake-server", client.ServerInfo.Name);
        Assert.Equal("Usa add para sumar.", client.Instructions);

        var infos = await client.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["add", "fail"], infos.Select(t => t.Name)); // dos páginas

        var result = await client.CallToolAsync("add", JsonElement.Parse("""{"a":2,"b":3}"""), TestContext.Current.CancellationToken);
        Assert.Equal("5", result.Text);
        Assert.False(result.IsError);

        var ping = await server.PingResponse.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("srv-1", (string?)ping["id"]);
        Assert.NotNull(ping["result"]);
        Assert.Contains("notifications/initialized", server.ReceivedMethods);
    }

    [Fact]
    public async Task McpToolsWorkInsideTheAgentLoop()
    {
        await using var server = new FakeMcpServer();
        await using var client = await McpClient.ConnectAsync(server.CreateTransport(), cancellationToken: TestContext.Current.CancellationToken);
        var tools = await client.GetToolsAsync(prefix: "calc_", cancellationToken: TestContext.Current.CancellationToken);

        var model = new MockLanguageModel()
            .ReturnsToolCall("calc_add", new { a = 20, b = 22 }, "c1")
            .ReturnsToolCall("calc_fail", new { }, "c2")
            .ReturnsText("La suma es 42");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "Suma 20 y 22",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(5),
        });

        Assert.Equal("La suma es 42", result.Text);
        Assert.Equal(["calc_add", "calc_fail"], model.Requests[0].Tools!.Select(t => t.Name));
        Assert.Equal(42, result.Steps[0].ToolResults[0].Output.GetProperty("sum").GetDouble());
        var failed = result.Steps[1].ToolResults[0];
        Assert.True(failed.IsError);
        Assert.Equal("boom", failed.Output.GetString());
    }

    [Fact]
    public async Task ConnectsOverStreamableHttpWithSession()
    {
        var handler = new FakeHandler()
            .Json("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"http-server"}}}""",
                headers: new Dictionary<string, string> { ["Mcp-Session-Id"] = "sess-123" })
            .Accepted()
            .Sse("event: message\ndata: " + """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"echo","inputSchema":{"type":"object"}}]}}""")
            .Json("""{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"hola"}]}}""")
            .Accepted();

        var transport = new HttpMcpTransport(new Uri("https://mcp.example.com/mcp"), new HttpClient(handler), new Dictionary<string, string> { ["Authorization"] = "Bearer t" });
        await using (var client = await McpClient.ConnectAsync(transport, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal("http-server", client.ServerInfo.Name);
            var tools = await client.GetToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var output = await tools[0].Execute!(JsonElement.Parse("{}"), new ToolExecutionContext { ToolCallId = "x", ToolName = "echo", Messages = [] }, CancellationToken.None);
            Assert.Equal("hola", output);
        }

        Assert.Equal("Bearer t", handler.Requests[0].Request.Headers.Authorization!.ToString());
        Assert.False(handler.Requests[0].Request.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal("sess-123", handler.Requests[2].Request.Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Equal("2025-06-18", handler.Requests[2].Request.Headers.GetValues("MCP-Protocol-Version").Single());
        Assert.Equal("notifications/initialized", (string?)handler.Requests[1].Body["method"]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Request.Method); // cierre de sesión
    }

    [Fact]
    public async Task LaunchesRealStdioProcess()
    {
        var python = FindExecutable("python3");
        Assert.SkipWhen(python is null, "python3 no instalado");

        var script = Path.Combine(Path.GetTempPath(), $"mcp-server-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(script, """
            import json, sys
            for line in sys.stdin:
                msg = json.loads(line)
                if "id" not in msg:
                    continue
                m = msg["method"]
                if m == "initialize":
                    res = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "py"}}
                elif m == "tools/list":
                    res = {"tools": [{"name": "upper", "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}}]}
                elif m == "tools/call":
                    res = {"content": [{"type": "text", "text": msg["params"]["arguments"]["text"].upper()}]}
                else:
                    res = {}
                print("log en stderr", file=sys.stderr, flush=True)
                print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": res}), flush=True)
            """, TestContext.Current.CancellationToken);
        try
        {
            var logs = new List<string>();
            await using var client = await McpClient.ConnectAsync(new StdioMcpTransport(python, ["-u", script]) { OnStderr = l => { lock (logs) { logs.Add(l); } } }, cancellationToken: TestContext.Current.CancellationToken);
            var result = await client.CallToolAsync("upper", JsonElement.Parse("""{"text":"hola mcp"}"""), TestContext.Current.CancellationToken);
            Assert.Equal("py", client.ServerInfo.Name);
            Assert.Equal("HOLA MCP", result.Text);
        }
        finally
        {
            File.Delete(script);
        }
    }

    internal static string? FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
}
