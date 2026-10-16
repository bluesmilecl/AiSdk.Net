using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Internal;

namespace AiSdk.Mcp;

/// <summary>Opciones del cliente MCP.</summary>
public sealed class McpClientOptions
{
    public string ClientName { get; init; } = "AiSdk.NET";

    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>Versión de protocolo solicitada al servidor.</summary>
    public string ProtocolVersion { get; init; } = McpClient.LatestProtocolVersion;

    /// <summary>Tiempo máximo de espera por respuesta.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Raíces que el servidor puede consultar con <c>roots/list</c> (declara la capacidad <c>roots</c>).</summary>
    public IReadOnlyList<McpRoot>? Roots { get; init; }

    /// <summary>
    /// Modelo con el que se atienden las peticiones <c>sampling/createMessage</c> del servidor
    /// (declara la capacidad <c>sampling</c>). Alternativa: <see cref="SamplingHandler"/>.
    /// </summary>
    public ILanguageModel? SamplingModel { get; init; }

    /// <summary>Atiende <c>sampling/createMessage</c> con lógica propia (p. ej. pedir aprobación al usuario).</summary>
    public Func<McpSamplingRequest, CancellationToken, Task<McpSamplingResult>>? SamplingHandler { get; init; }

    /// <summary>Atiende <c>elicitation/create</c>: el servidor pide datos al usuario (declara la capacidad <c>elicitation</c>).</summary>
    public Func<McpElicitationRequest, CancellationToken, Task<McpElicitationResult>>? ElicitationHandler { get; init; }
}

/// <summary>Información del servidor devuelta en <c>initialize</c>.</summary>
public sealed record McpServerInfo(string Name, string? Version);

/// <summary>Herramienta publicada por un servidor MCP.</summary>
public sealed record McpToolInfo(string Name, string? Description, JsonElement InputSchema)
{
    public string? Title { get; init; }
}

/// <summary>Resultado de <c>tools/call</c>.</summary>
public sealed record McpCallToolResult(JsonArray Content, bool IsError, JsonNode? StructuredContent)
{
    /// <summary>Texto concatenado de los bloques <c>text</c>.</summary>
    public string Text => string.Join("\n", Content.OfType<JsonObject>().Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
}

/// <summary>Error JSON-RPC devuelto por el servidor MCP.</summary>
public sealed class McpException(string message, int code = 0) : AiSdkException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Cliente MCP (equivalente a <c>experimental_createMCPClient</c> del AI SDK).
/// Se conecta a un servidor, lista sus herramientas y las expone como <see cref="Tool"/>
/// para usarlas en <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/> o en agentes.
/// </summary>
/// <example>
/// <code>
/// await using var mcp = await McpClient.ConnectAsync(
///     new StdioMcpTransport("npx", ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]));
/// var tools = await mcp.GetToolsAsync();
/// var agent = new ToolLoopAgent(new AgentSettings { Model = model, Tools = tools });
/// </code>
/// </example>
public sealed class McpClient : IAsyncDisposable
{
    public const string LatestProtocolVersion = "2025-06-18";

    private readonly IMcpTransport _transport;
    private readonly McpClientOptions _options;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _dispatchLoop;
    private long _nextId;

    private IReadOnlyList<McpRoot>? _roots;

    private McpClient(IMcpTransport transport, McpClientOptions options)
    {
        _transport = transport;
        _options = options;
        _roots = options.Roots;
    }

    public McpServerInfo ServerInfo { get; private set; } = new("unknown", null);

    public JsonObject ServerCapabilities { get; private set; } = [];

    /// <summary>Instrucciones opcionales del servidor (pueden añadirse al prompt de sistema).</summary>
    public string? Instructions { get; private set; }

    public string ProtocolVersion { get; private set; } = LatestProtocolVersion;

    /// <summary>Se invoca con cada notificación del servidor (método y parámetros).</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Conecta e inicializa la sesión MCP.</summary>
    public static async Task<McpClient> ConnectAsync(IMcpTransport transport, McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = new McpClient(transport, options ?? new McpClientOptions());
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _dispatchLoop = Task.Run(DispatchLoopAsync);

        var capabilities = new JsonObject();
        if (_roots is not null)
        {
            capabilities["roots"] = new JsonObject { ["listChanged"] = true };
        }

        if (_options.SamplingModel is not null || _options.SamplingHandler is not null)
        {
            capabilities["sampling"] = new JsonObject();
        }

        if (_options.ElicitationHandler is not null)
        {
            capabilities["elicitation"] = new JsonObject();
        }

        var result = await SendRequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = _options.ProtocolVersion,
            ["capabilities"] = capabilities,
            ["clientInfo"] = new JsonObject { ["name"] = _options.ClientName, ["version"] = _options.ClientVersion },
        }, cancellationToken).ConfigureAwait(false);

        ProtocolVersion = (string?)result?["protocolVersion"] ?? _options.ProtocolVersion;
        ServerCapabilities = result?["capabilities"] as JsonObject ?? [];
        Instructions = (string?)result?["instructions"];
        ServerInfo = new McpServerInfo((string?)result?["serverInfo"]?["name"] ?? "unknown", (string?)result?["serverInfo"]?["version"]);
        _transport.SetProtocolVersion(ProtocolVersion);

        await SendNotificationAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lista todas las herramientas del servidor (recorre la paginación).</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        do
        {
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            var result = await SendRequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);
            foreach (var tool in (result?["tools"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var schema = tool["inputSchema"] is JsonObject s ? JsonSerializer.SerializeToElement(s) : JsonSchema.Parse("""{"type":"object","properties":{}}""");
                tools.Add(new McpToolInfo((string?)tool["name"] ?? string.Empty, (string?)tool["description"], schema) { Title = (string?)tool["title"] });
            }

            cursor = (string?)result?["nextCursor"];
        }
        while (!string.IsNullOrEmpty(cursor));

        return tools;
    }

    /// <summary>Invoca una herramienta del servidor.</summary>
    public async Task<McpCallToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Object ? JsonUtil.ToNode(arguments) : new JsonObject(),
        }, cancellationToken).ConfigureAwait(false);

        return new McpCallToolResult(
            result?["content"]?.DeepClone() as JsonArray ?? [],
            (bool?)result?["isError"] ?? false,
            result?["structuredContent"]?.DeepClone());
    }

    /// <summary>
    /// Devuelve las herramientas del servidor como <see cref="Tool"/> de AiSdk.
    /// </summary>
    /// <param name="include">Si se indica, solo se incluyen estas herramientas.</param>
    /// <param name="prefix">Prefijo opcional para evitar colisiones de nombre entre servidores (p. ej. <c>"github_"</c>).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    public async Task<IReadOnlyList<Tool>> GetToolsAsync(IEnumerable<string>? include = null, string? prefix = null, CancellationToken cancellationToken = default)
    {
        var filter = include?.ToHashSet(StringComparer.Ordinal);
        var infos = await ListToolsAsync(cancellationToken).ConfigureAwait(false);
        return infos
            .Where(t => filter is null || filter.Contains(t.Name))
            .Select(info => Tool.FromSchema(
                (prefix ?? string.Empty) + info.Name,
                info.Description ?? info.Title ?? info.Name,
                info.InputSchema,
                async (input, _, ct) =>
                {
                    var result = await CallToolAsync(info.Name, input, ct).ConfigureAwait(false);
                    if (result.IsError)
                    {
                        throw new McpException(result.Text.Length > 0 ? result.Text : $"La herramienta MCP '{info.Name}' devolvió un error.");
                    }

                    return ToOutput(result);
                }))
            .ToList();
    }

    private static object? ToOutput(McpCallToolResult result)
    {
        if (result.StructuredContent is not null)
        {
            return result.StructuredContent;
        }

        var blocks = result.Content.OfType<JsonObject>().ToList();
        return blocks.All(b => (string?)b["type"] == "text") ? result.Text : result.Content;
    }

    // ------------------------------------------------------------------
    // Recursos
    // ------------------------------------------------------------------

    /// <summary>Lista los recursos del servidor (recorre la paginación).</summary>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
        (await ListAllAsync("resources/list", "resources", cancellationToken).ConfigureAwait(false))
            .Select(r => new McpResource((string?)r["uri"] ?? string.Empty, (string?)r["name"] ?? string.Empty)
            {
                Title = (string?)r["title"],
                Description = (string?)r["description"],
                MimeType = (string?)r["mimeType"],
            })
            .ToList();

    /// <summary>Lista las plantillas de recursos del servidor.</summary>
    public async Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken = default) =>
        (await ListAllAsync("resources/templates/list", "resourceTemplates", cancellationToken).ConfigureAwait(false))
            .Select(r => new McpResourceTemplate((string?)r["uriTemplate"] ?? string.Empty, (string?)r["name"] ?? string.Empty)
            {
                Description = (string?)r["description"],
                MimeType = (string?)r["mimeType"],
            })
            .ToList();

    /// <summary>Lee un recurso por su URI.</summary>
    public async Task<IReadOnlyList<McpResourceContent>> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("resources/read", new JsonObject { ["uri"] = uri }, cancellationToken).ConfigureAwait(false);
        return (result?["contents"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(c => new McpResourceContent(
                (string?)c["uri"] ?? uri,
                (string?)c["mimeType"],
                (string?)c["text"],
                (string?)c["blob"] is { } blob ? Convert.FromBase64String(blob) : null))
            .ToList();
    }

    /// <summary>
    /// Lee un recurso y lo devuelve como partes de contenido (texto o archivo) para añadirlo a un mensaje.
    /// </summary>
    public async Task<IReadOnlyList<ContentPart>> ReadResourceAsPartsAsync(string uri, CancellationToken cancellationToken = default) =>
        (await ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false))
            .Select(c => c.Text is not null
                ? (ContentPart)new TextPart(c.Text)
                : (c.MimeType ?? string.Empty).StartsWith("image/", StringComparison.Ordinal)
                    ? new ImagePart { Data = c.Blob, MediaType = c.MimeType! }
                    : new FilePart { Data = c.Blob, MediaType = c.MimeType ?? "application/octet-stream", Filename = c.Uri })
            .ToList();

    // ------------------------------------------------------------------
    // Prompts
    // ------------------------------------------------------------------

    /// <summary>Lista los prompts del servidor.</summary>
    public async Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
        (await ListAllAsync("prompts/list", "prompts", cancellationToken).ConfigureAwait(false))
            .Select(p => new McpPrompt(
                (string?)p["name"] ?? string.Empty,
                (string?)p["description"],
                (p["arguments"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(a => new McpPromptArgument((string?)a["name"] ?? string.Empty, (string?)a["description"], (bool?)a["required"] ?? false))
                    .ToList())
            {
                Title = (string?)p["title"],
            })
            .ToList();

    /// <summary>Obtiene un prompt con sus argumentos y lo convierte en mensajes del modelo.</summary>
    public async Task<McpPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, string>? arguments = null, CancellationToken cancellationToken = default)
    {
        var args = new JsonObject();
        foreach (var (key, value) in arguments ?? new Dictionary<string, string>())
        {
            args[key] = value;
        }

        var result = await SendRequestAsync("prompts/get", new JsonObject { ["name"] = name, ["arguments"] = args }, cancellationToken).ConfigureAwait(false);
        return new McpPromptResult(
            (string?)result?["description"],
            (result?["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(McpConvert.ToModelMessage).ToList());
    }

    /// <summary>Cambia las raíces expuestas y avisa al servidor (<c>notifications/roots/list_changed</c>).</summary>
    public Task SetRootsAsync(IReadOnlyList<McpRoot> roots, CancellationToken cancellationToken = default)
    {
        _roots = roots;
        return SendNotificationAsync("notifications/roots/list_changed", null, cancellationToken);
    }

    private async Task<List<JsonObject>> ListAllAsync(string method, string property, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            var result = await SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            items.AddRange((result?[property] as JsonArray ?? []).OfType<JsonObject>().Select(i => (JsonObject)i.DeepClone()));
            cursor = (string?)result?["nextCursor"];
        }
        while (!string.IsNullOrEmpty(cursor));

        return items;
    }

    /// <summary>Envía una petición JSON-RPC arbitraria y devuelve su <c>result</c>.</summary>
    public async Task<JsonNode?> SendRequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new McpException($"El servidor MCP no respondió a '{method}' en {_options.RequestTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return _transport.SendAsync(message, cancellationToken);
    }

    private async Task DispatchLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var message in _transport.Messages.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var hasMethod = message["method"] is not null;
                var idNode = message["id"];
                if (!hasMethod && idNode is not null)
                {
                    HandleResponse(message, idNode);
                }
                else if (hasMethod && idNode is not null)
                {
                    // Sampling y elicitation pueden tardar: se atienden sin bloquear el resto de mensajes.
                    _ = Task.Run(() => HandleServerRequestAsync(message, idNode));
                }
                else if (hasMethod)
                {
                    NotificationReceived?.Invoke((string)message["method"]!, message["params"]);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // El transporte se cerró: las peticiones pendientes fallan.
        foreach (var (_, pending) in _pending)
        {
            pending.TrySetException(new McpException("La conexión con el servidor MCP se cerró." + (failure is null ? string.Empty : $" {failure.Message}")));
        }
    }

    private void HandleResponse(JsonObject message, JsonNode idNode)
    {
        long id;
        try
        {
            id = idNode.GetValueKind() == JsonValueKind.String ? long.Parse((string)idNode!, System.Globalization.CultureInfo.InvariantCulture) : (long)idNode;
        }
        catch (Exception)
        {
            return;
        }

        if (!_pending.TryGetValue(id, out var tcs))
        {
            return;
        }

        if (message["error"] is JsonObject error)
        {
            tcs.TrySetException(new McpException((string?)error["message"] ?? "Error MCP", (int?)error["code"] ?? 0));
        }
        else
        {
            tcs.TrySetResult(message["result"]?.DeepClone());
        }
    }

    private async Task HandleServerRequestAsync(JsonObject message, JsonNode idNode)
    {
        var method = (string?)message["method"];
        var parameters = message["params"] as JsonObject ?? [];
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone() };
        try
        {
            JsonNode? result = method switch
            {
                "ping" => new JsonObject(),
                "roots/list" when _roots is not null => new JsonObject
                {
                    ["roots"] = new JsonArray(_roots.Select(r =>
                    {
                        var root = new JsonObject { ["uri"] = r.Uri };
                        if (r.Name is not null)
                        {
                            root["name"] = r.Name;
                        }

                        return (JsonNode?)root;
                    }).ToArray()),
                },
                "sampling/createMessage" when _options.SamplingModel is not null || _options.SamplingHandler is not null =>
                    await SampleAsync(parameters).ConfigureAwait(false),
                "elicitation/create" when _options.ElicitationHandler is not null =>
                    await ElicitAsync(parameters).ConfigureAwait(false),
                _ => null,
            };

            if (result is null)
            {
                response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Método no soportado: {method}" };
            }
            else
            {
                response["result"] = result;
            }
        }
        catch (Exception ex)
        {
            response["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
        }

        try
        {
            await _transport.SendAsync(response, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // El transporte pudo cerrarse mientras se atendía la petición.
        }
    }

    private async Task<JsonNode> SampleAsync(JsonObject parameters)
    {
        var request = new McpSamplingRequest(
            (parameters["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(McpConvert.ToModelMessage).ToList(),
            (string?)parameters["systemPrompt"],
            (int?)parameters["maxTokens"],
            (double?)parameters["temperature"],
            (parameters["stopSequences"] as JsonArray)?.Select(s => (string?)s).OfType<string>().ToList(),
            parameters["modelPreferences"]?.DeepClone());

        McpSamplingResult sampled;
        if (_options.SamplingHandler is not null)
        {
            sampled = await _options.SamplingHandler(request, _cts.Token).ConfigureAwait(false);
        }
        else
        {
            var model = _options.SamplingModel!;
            var generated = await Ai.GenerateTextAsync(new GenerateTextOptions
            {
                Model = model,
                System = request.SystemPrompt,
                Messages = request.Messages,
                MaxOutputTokens = request.MaxTokens,
                Temperature = request.Temperature,
                StopSequences = request.StopSequences,
                CancellationToken = _cts.Token,
            }).ConfigureAwait(false);
            sampled = new McpSamplingResult(
                generated.Text,
                generated.Response?.ModelId ?? model.ModelId,
                generated.FinishReason == FinishReason.Length ? "maxTokens" : "endTurn");
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = sampled.Text },
            ["model"] = sampled.Model,
            ["stopReason"] = sampled.StopReason,
        };
    }

    private async Task<JsonNode> ElicitAsync(JsonObject parameters)
    {
        var schema = parameters["requestedSchema"] is JsonObject s ? JsonSerializer.SerializeToElement(s) : Internal.JsonUtil.EmptyObject;
        var result = await _options.ElicitationHandler!(new McpElicitationRequest((string?)parameters["message"] ?? string.Empty, schema), _cts.Token).ConfigureAwait(false);
        var node = new JsonObject { ["action"] = result.Action };
        if (result.Content is not null)
        {
            node["content"] = result.Content.DeepClone();
        }

        return node;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_dispatchLoop is not null)
        {
            await _dispatchLoop.ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}
