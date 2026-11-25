using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Internal;

namespace AiSdk.Mcp;

/// <summary>MCP client options.</summary>
public sealed class McpClientOptions
{
    public string ClientName { get; init; } = "AiSdk.NET";

    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>Protocol version requested from the server.</summary>
    public string ProtocolVersion { get; init; } = McpClient.LatestProtocolVersion;

    /// <summary>
    /// Maximum time for each request, end to end: sending, reading the response body
    /// (also with Streamable HTTP) and waiting for the response.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Roots the server can query with <c>roots/list</c> (declares the <c>roots</c> capability).</summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public IReadOnlyList<McpRoot>? Roots { get; init; }

    /// <summary>
    /// Model used to serve the server's <c>sampling/createMessage</c> requests
    /// (declares the <c>sampling</c> capability). Requires <see cref="ApproveSampling"/>: the server can never
    /// use the model without the user's consent. Alternative: <see cref="SamplingHandler"/>.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public ILanguageModel? SamplingModel { get; init; }

    /// <summary>
    /// Consent for each sampling request served with <see cref="SamplingModel"/> (human-in-the-loop,
    /// as required by the MCP specification): show the user the system prompt and the server's messages and
    /// return the request to execute (you may edit it) or <c>null</c> to reject it.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Func<McpSamplingRequest, CancellationToken, Task<McpSamplingRequest?>>? ApproveSampling { get; init; }

    /// <summary>
    /// Optional review of the generated response before it is sent to the server: return the response
    /// (you may edit it) or <c>null</c> to withhold it (the server receives a rejection).
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Func<McpSamplingRequest, McpSamplingResult, CancellationToken, Task<McpSamplingResult?>>? ReviewSamplingResult { get; init; }

    /// <summary>
    /// Serves <c>sampling/createMessage</c> with custom logic. The implementer is responsible for obtaining the user's
    /// consent; to reject, throw <see cref="McpRequestRejectedException"/>.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Func<McpSamplingRequest, CancellationToken, Task<McpSamplingResult>>? SamplingHandler { get; init; }

    /// <summary>
    /// Maximum tokens the server may request in each sampling call: a larger (or missing) <c>maxTokens</c> is clamped
    /// to this value before asking for approval. Defaults to 4096.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public int MaxSamplingTokens { get; init; } = 4096;

    /// <summary>
    /// Server requests (sampling, elicitation, roots, ping) served concurrently. Requests arriving above
    /// the limit are rejected with an error instead of being queued. Defaults to 4.
    /// </summary>
    public int MaxConcurrentServerRequests { get; init; } = 4;

    /// <summary>Serves <c>elicitation/create</c>: the server asks the user for data (declares the <c>elicitation</c> capability).</summary>
    public Func<McpElicitationRequest, CancellationToken, Task<McpElicitationResult>>? ElicitationHandler { get; init; }

    /// <summary>
    /// If true, every tool returned by <see cref="McpClient.GetToolsAsync"/> requires human approval
    /// (<see cref="Tool.NeedsApproval"/>, which is resolved with <c>OnToolApproval</c>).
    /// </summary>
    public bool RequireToolApproval { get; init; }

    /// <summary>
    /// Prefix for this server's tool names in <see cref="McpClient.GetToolsAsync"/>
    /// (e.g. <c>"github_"</c>) when no explicit one is passed. Avoids collisions between servers.
    /// </summary>
    public string? ToolNamePrefix { get; init; }

    /// <summary>What to do if a tool's definition changes after it was pinned. Defaults to <see cref="McpToolChangePolicy.Reject"/>.</summary>
    public McpToolChangePolicy ToolChangePolicy { get; init; } = McpToolChangePolicy.Reject;

    /// <summary>
    /// Hashes of definitions approved in previous sessions (name → hash, see <see cref="McpClient.ToolHashes"/>).
    /// They allow detecting changes across connections, not just within one.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PinnedToolHashes { get; init; }

    /// <summary>Maximum number of pages traversed in paginated listings (<c>tools/list</c>, <c>resources/list</c>...). Defaults to 100.</summary>
    public int MaxListPages { get; init; } = 100;
}

/// <summary>Server information returned by <c>initialize</c>.</summary>
public sealed record McpServerInfo(string Name, string? Version);

/// <summary>Tool published by an MCP server.</summary>
public sealed record McpToolInfo(string Name, string? Description, JsonElement InputSchema)
{
    public string? Title { get; init; }

    /// <summary>SHA-256 (hex) of the canonical definition: name, title, description, input schema and annotations.</summary>
    public string DefinitionHash { get; init; } = string.Empty;

    /// <summary>True if the definition differs from the pinned one and <see cref="McpClientOptions.ToolChangePolicy"/> is <see cref="McpToolChangePolicy.Reject"/>.</summary>
    public bool DefinitionChanged { get; init; }
}

/// <summary>Result of <c>tools/call</c>.</summary>
public sealed record McpCallToolResult(JsonArray Content, bool IsError, JsonNode? StructuredContent)
{
    /// <summary>Concatenated text of the <c>text</c> blocks.</summary>
    public string Text => string.Join("\n", Content.OfType<JsonObject>().Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
}

/// <summary>JSON-RPC error returned by the MCP server.</summary>
public class McpException(string message, int code = 0) : AiSdkException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// MCP client (equivalent to the AI SDK's <c>experimental_createMCPClient</c>).
/// Connects to a server, lists its tools and exposes them as <see cref="Tool"/>
/// for use in <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/> or in agents.
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
    private readonly ConcurrentDictionary<string, string> _toolHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _changedTools = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _dispatchLoop;
    private long _nextId;
    private int _activeServerRequests;
    private int _disposed;

    private IReadOnlyList<McpRoot>? _roots;

    private McpClient(IMcpTransport transport, McpClientOptions options)
    {
        _transport = transport;
        _options = options;
        _roots = options.Roots;
        foreach (var (name, hash) in options.PinnedToolHashes ?? new Dictionary<string, string>())
        {
            _toolHashes[name] = hash;
        }
    }

    public McpServerInfo ServerInfo { get; private set; } = new("unknown", null);

    public JsonObject ServerCapabilities { get; private set; } = [];

    /// <summary>Optional server instructions (they can be appended to the system prompt).</summary>
    public string? Instructions { get; private set; }

    public string ProtocolVersion { get; private set; } = LatestProtocolVersion;

    /// <summary>
    /// Pinned hashes of the tool definitions (name → SHA-256). Store them and pass them in
    /// <see cref="McpClientOptions.PinnedToolHashes"/> to detect changes across sessions.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolHashes => new Dictionary<string, string>(_toolHashes, StringComparer.Ordinal);

    /// <summary>Invoked with each server notification (method and parameters).</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Invoked when an already pinned tool shows up with a different definition (possible "rug pull").</summary>
    public event Action<McpToolDefinitionChange>? ToolDefinitionChanged;

    /// <summary>
    /// Invoked when serving a server request fails (method and exception). The server only receives a
    /// generic message: the details are provided here for the host's log.
    /// </summary>
    public event Action<string, Exception>? ServerRequestFailed;

    /// <summary>Connects and initializes the MCP session.</summary>
    public static async Task<McpClient> ConnectAsync(IMcpTransport transport, McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new McpClientOptions();
        Validate(options);
        var client = new McpClient(transport, options);
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

    private static void Validate(McpClientOptions options)
    {
        if (options.SamplingModel is not null && options.SamplingHandler is null && options.ApproveSampling is null)
        {
            throw new ArgumentException(
                "SamplingModel requiere ApproveSampling (consentimiento del usuario para cada petición del servidor) o un SamplingHandler propio. " +
                "Un servidor MCP no puede usar su modelo sin aprobación.",
                nameof(options));
        }

        if (options.MaxSamplingTokens <= 0 || options.MaxConcurrentServerRequests <= 0 || options.MaxListPages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSamplingTokens, MaxConcurrentServerRequests y MaxListPages deben ser mayores que cero.");
        }
    }

    private bool SamplingEnabled => _options.SamplingHandler is not null || (_options.SamplingModel is not null && _options.ApproveSampling is not null);

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        // El bucle vive lo que dure el cliente (se detiene en DisposeAsync), no lo que dure la inicialización.
        _dispatchLoop = Task.Run(DispatchLoopAsync, CancellationToken.None);

        var capabilities = new JsonObject();
        if (_roots is not null)
        {
            capabilities["roots"] = new JsonObject { ["listChanged"] = true };
        }

        if (SamplingEnabled)
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

    /// <summary>
    /// Lists all the server's tools (traversing pagination). Each definition is pinned by its hash the
    /// first time it appears; if it changes in a later listing, <see cref="McpClientOptions.ToolChangePolicy"/> is applied.
    /// </summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<McpToolInfo>();
        foreach (var tool in await ListAllAsync("tools/list", "tools", cancellationToken).ConfigureAwait(false))
        {
            var schema = tool["inputSchema"] is JsonObject s ? JsonSerializer.SerializeToElement(s) : JsonSchema.Parse("""{"type":"object","properties":{}}""");
            var info = new McpToolInfo((string?)tool["name"] ?? string.Empty, (string?)tool["description"], schema)
            {
                Title = (string?)tool["title"],
                DefinitionHash = HashDefinition(tool),
            };
            tools.Add(info with { DefinitionChanged = Pin(info) });
        }

        return tools;
    }

    /// <summary>Pins the definition or compares it with the pinned one. Returns true if the tool is blocked.</summary>
    private bool Pin(McpToolInfo info)
    {
        var pinned = _toolHashes.GetOrAdd(info.Name, info.DefinitionHash);
        if (pinned == info.DefinitionHash)
        {
            return _changedTools.ContainsKey(info.Name);
        }

        if (_options.ToolChangePolicy == McpToolChangePolicy.Accept)
        {
            _toolHashes[info.Name] = info.DefinitionHash;
        }
        else
        {
            // Bloqueo persistente en este cliente: volver a la definición original no basta para desbloquearla.
            _changedTools[info.Name] = 0;
        }

        ToolDefinitionChanged?.Invoke(new McpToolDefinitionChange(info.Name, pinned, info.DefinitionHash, info));
        return _options.ToolChangePolicy == McpToolChangePolicy.Reject;
    }

    /// <summary>SHA-256 of the canonical form (sorted keys) of what the model sees plus the annotations.</summary>
    private static string HashDefinition(JsonObject tool)
    {
        var definition = new JsonObject();
        foreach (var key in new[] { "name", "title", "description", "inputSchema", "annotations" })
        {
            if (tool[key] is { } value)
            {
                definition[key] = Canonicalize(value);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition.ToJsonString())));
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        _ => node?.DeepClone(),
    };

    /// <summary>
    /// Invokes a server tool. The call is rejected if its definition changed after it was pinned and the policy is
    /// <see cref="McpToolChangePolicy.Reject"/>.
    /// </summary>
    public async Task<McpCallToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (_changedTools.ContainsKey(name))
        {
            throw new McpException($"La definición de la herramienta MCP '{name}' cambió desde que se fijó; la llamada se rechaza. Revise el servidor y vuelva a conectar si confía en la nueva definición.");
        }

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
    /// Returns the server's tools as AiSdk <see cref="Tool"/> instances. Omits tools blocked by a definition
    /// change and, with <see cref="McpClientOptions.RequireToolApproval"/>, all of them require human approval.
    /// </summary>
    /// <param name="include">If specified, only these tools are included.</param>
    /// <param name="prefix">Prefix to avoid name collisions between servers (e.g. <c>"github_"</c>); defaults to <see cref="McpClientOptions.ToolNamePrefix"/>.</param>
    /// <param name="existingTools">Tools they will be combined with (local or from other servers): if a name matches, <see cref="McpException"/> is thrown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<Tool>> GetToolsAsync(IEnumerable<string>? include = null, string? prefix = null, IEnumerable<Tool>? existingTools = null, CancellationToken cancellationToken = default)
    {
        var filter = include?.ToHashSet(StringComparer.Ordinal);
        var reserved = existingTools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var effectivePrefix = prefix ?? _options.ToolNamePrefix ?? string.Empty;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tools = new List<Tool>();
        foreach (var info in await ListToolsAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((filter is not null && !filter.Contains(info.Name)) || info.DefinitionChanged)
            {
                continue;
            }

            var name = effectivePrefix + info.Name;
            if (reserved.Contains(name))
            {
                throw new McpException($"La herramienta MCP '{name}' del servidor '{ServerInfo.Name}' tiene el mismo nombre que otra herramienta. Use un prefijo (prefix o McpClientOptions.ToolNamePrefix).");
            }

            if (!names.Add(name))
            {
                throw new McpException($"El servidor MCP '{ServerInfo.Name}' publicó dos herramientas con el nombre '{info.Name}'.");
            }

            var tool = Tool.FromSchema(
                name,
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
                });
            tools.Add(_options.RequireToolApproval ? tool.RequireApproval() : tool);
        }

        return tools;
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

    /// <summary>Lists the server's resources (traversing pagination).</summary>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
        (await ListAllAsync("resources/list", "resources", cancellationToken).ConfigureAwait(false))
            .Select(r => new McpResource((string?)r["uri"] ?? string.Empty, (string?)r["name"] ?? string.Empty)
            {
                Title = (string?)r["title"],
                Description = (string?)r["description"],
                MimeType = (string?)r["mimeType"],
            })
            .ToList();

    /// <summary>Lists the server's resource templates.</summary>
    public async Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken = default) =>
        (await ListAllAsync("resources/templates/list", "resourceTemplates", cancellationToken).ConfigureAwait(false))
            .Select(r => new McpResourceTemplate((string?)r["uriTemplate"] ?? string.Empty, (string?)r["name"] ?? string.Empty)
            {
                Description = (string?)r["description"],
                MimeType = (string?)r["mimeType"],
            })
            .ToList();

    /// <summary>Reads a resource by its URI.</summary>
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
    /// Reads a resource and returns it as content parts (text or file) to add to a message.
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

    /// <summary>Lists the server's prompts.</summary>
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

    /// <summary>Gets a prompt with its arguments and converts it into model messages.</summary>
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

    /// <summary>Changes the exposed roots and notifies the server (<c>notifications/roots/list_changed</c>).</summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Task SetRootsAsync(IReadOnlyList<McpRoot> roots, CancellationToken cancellationToken = default)
    {
        _roots = roots;
        return SendNotificationAsync("notifications/roots/list_changed", null, cancellationToken);
    }

    /// <summary>
    /// Traverses a paginated listing. Fails if the server repeats a cursor (loop) or if
    /// <see cref="McpClientOptions.MaxListPages"/> is exceeded.
    /// </summary>
    private async Task<List<JsonObject>> ListAllAsync(string method, string property, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 1; ; page++)
        {
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            var result = await SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            items.AddRange((result?[property] as JsonArray ?? []).OfType<JsonObject>().Select(i => (JsonObject)i.DeepClone()));
            cursor = (string?)result?["nextCursor"];
            if (string.IsNullOrEmpty(cursor))
            {
                return items;
            }

            if (!seen.Add(cursor))
            {
                throw new McpException($"El servidor MCP repitió el cursor de paginación de '{method}' (bucle).");
            }

            if (page >= _options.MaxListPages)
            {
                throw new McpException($"'{method}' superó el máximo de {_options.MaxListPages} páginas (McpClientOptions.MaxListPages).");
            }
        }
    }

    /// <summary>
    /// Sends an arbitrary JSON-RPC request and returns its <c>result</c>. <see cref="McpClientOptions.RequestTimeout"/>
    /// covers sending, reading the body and waiting for the response.
    /// </summary>
    public async Task<JsonNode?> SendRequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await _transport.SendAsync(message, timeout.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"El servidor MCP no respondió a '{method}' en {_options.RequestTimeout.TotalSeconds:0.###} s.");
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
                    if (Interlocked.Increment(ref _activeServerRequests) > _options.MaxConcurrentServerRequests)
                    {
                        // Un servidor no puede acumular trabajo sin límite en el cliente.
                        Interlocked.Decrement(ref _activeServerRequests);
                        await SendErrorAsync(idNode, -32000, "Demasiadas peticiones simultáneas del servidor.").ConfigureAwait(false);
                        continue;
                    }

                    // Sampling y elicitation pueden tardar: se atienden sin bloquear el resto de mensajes.
                    _ = Task.Run(async () =>
                    {
                        JsonObject response;
                        try
                        {
                            response = await HandleServerRequestAsync(message, idNode).ConfigureAwait(false);
                        }
                        finally
                        {
                            // El cupo se libera antes de responder: cuando el servidor ve la respuesta, ya puede pedir otra.
                            Interlocked.Decrement(ref _activeServerRequests);
                        }

                        await TrySendAsync(response).ConfigureAwait(false);
                    });
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

    private async Task<JsonObject> HandleServerRequestAsync(JsonObject message, JsonNode idNode)
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
                "sampling/createMessage" when SamplingEnabled => await SampleAsync(parameters).ConfigureAwait(false),
                "elicitation/create" when _options.ElicitationHandler is not null => await ElicitAsync(parameters).ConfigureAwait(false),
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
        catch (McpRequestRejectedException)
        {
            response["error"] = new JsonObject { ["code"] = -1, ["message"] = "El usuario rechazó la petición." };
        }
        catch (Exception ex)
        {
            // Nunca se envía ex.Message al servidor: puede contener texto del proveedor del modelo, rutas o secretos.
            ServerRequestFailed?.Invoke(method ?? string.Empty, ex);
            response["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Error interno del cliente MCP." };
        }

        return response;
    }

    private Task SendErrorAsync(JsonNode idNode, int code, string message) =>
        TrySendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private async Task TrySendAsync(JsonObject message)
    {
        try
        {
            await _transport.SendAsync(message, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // El transporte pudo cerrarse mientras se atendía la petición.
        }
    }

    private int ClampTokens(int? requested) => requested is { } n && n > 0 && n < _options.MaxSamplingTokens ? n : _options.MaxSamplingTokens;

    private async Task<JsonNode> SampleAsync(JsonObject parameters)
    {
        var request = new McpSamplingRequest(
            (parameters["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(McpConvert.ToModelMessage).ToList(),
            (string?)parameters["systemPrompt"],
            ClampTokens((int?)parameters["maxTokens"]),
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
            // Consentimiento: el usuario ve (y puede editar) el prompt de sistema y los mensajes del servidor.
            var approved = await _options.ApproveSampling!(request, _cts.Token).ConfigureAwait(false)
                           ?? throw new McpRequestRejectedException();
            approved = approved with { MaxTokens = ClampTokens(approved.MaxTokens) };
            var model = _options.SamplingModel!;
            var generated = await Ai.GenerateTextAsync(new GenerateTextOptions
            {
                Model = model,
                System = approved.SystemPrompt,
                Messages = approved.Messages,
                MaxOutputTokens = approved.MaxTokens,
                Temperature = approved.Temperature,
                StopSequences = approved.StopSequences,
                CancellationToken = _cts.Token,
            }).ConfigureAwait(false);
            sampled = new McpSamplingResult(
                generated.Text,
                generated.Response?.ModelId ?? model.ModelId,
                generated.FinishReason == FinishReason.Length ? "maxTokens" : "endTurn");
            if (_options.ReviewSamplingResult is { } review)
            {
                sampled = await review(approved, sampled, _cts.Token).ConfigureAwait(false) ?? throw new McpRequestRejectedException();
            }
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

    /// <summary>Closes the connection. Never throws (it also closes the HTTP session even if a stream failed).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (_dispatchLoop is not null)
        {
            try
            {
                await _dispatchLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
