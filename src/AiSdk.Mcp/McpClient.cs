using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Tiempo máximo de cada petición, de principio a fin: envío, lectura del cuerpo de la respuesta
    /// (también en Streamable HTTP) y espera de la respuesta.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Raíces que el servidor puede consultar con <c>roots/list</c> (declara la capacidad <c>roots</c>).</summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public IReadOnlyList<McpRoot>? Roots { get; init; }

    /// <summary>
    /// Modelo con el que se atienden las peticiones <c>sampling/createMessage</c> del servidor
    /// (declara la capacidad <c>sampling</c>). Exige <see cref="ApproveSampling"/>: el servidor nunca puede
    /// usar el modelo sin el consentimiento del usuario. Alternativa: <see cref="SamplingHandler"/>.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public ILanguageModel? SamplingModel { get; init; }

    /// <summary>
    /// Consentimiento para cada petición de sampling atendida con <see cref="SamplingModel"/> (human-in-the-loop,
    /// como exige la especificación de MCP): muestre al usuario el prompt de sistema y los mensajes del servidor y
    /// devuelva la petición que se ejecutará (puede editarla) o <c>null</c> para rechazarla.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Func<McpSamplingRequest, CancellationToken, Task<McpSamplingRequest?>>? ApproveSampling { get; init; }

    /// <summary>
    /// Revisión opcional de la respuesta generada antes de enviarla al servidor: devuelva la respuesta
    /// (puede editarla) o <c>null</c> para no enviarla (el servidor recibe un rechazo).
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Func<McpSamplingRequest, McpSamplingResult, CancellationToken, Task<McpSamplingResult?>>? ReviewSamplingResult { get; init; }

    /// <summary>
    /// Atiende <c>sampling/createMessage</c> con lógica propia. Quien lo implemente es responsable del consentimiento
    /// del usuario; para rechazar, lance <see cref="McpRequestRejectedException"/>.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Func<McpSamplingRequest, CancellationToken, Task<McpSamplingResult>>? SamplingHandler { get; init; }

    /// <summary>
    /// Máximo de tokens que puede pedir el servidor en cada sampling: un <c>maxTokens</c> mayor (o ausente) se recorta
    /// a este valor antes de pedir aprobación. Por defecto, 4096.
    /// </summary>
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public int MaxSamplingTokens { get; init; } = 4096;

    /// <summary>
    /// Peticiones del servidor (sampling, elicitation, roots, ping) atendidas a la vez. Las que lleguen por encima
    /// del límite se rechazan con un error en vez de acumularse. Por defecto, 4.
    /// </summary>
    public int MaxConcurrentServerRequests { get; init; } = 4;

    /// <summary>Atiende <c>elicitation/create</c>: el servidor pide datos al usuario (declara la capacidad <c>elicitation</c>).</summary>
    public Func<McpElicitationRequest, CancellationToken, Task<McpElicitationResult>>? ElicitationHandler { get; init; }

    /// <summary>
    /// Si es true, cada herramienta devuelta por <see cref="McpClient.GetToolsAsync"/> requiere aprobación humana
    /// (<see cref="Tool.NeedsApproval"/>, que se resuelve con <c>OnToolApproval</c>).
    /// </summary>
    public bool RequireToolApproval { get; init; }

    /// <summary>
    /// Prefijo para los nombres de las herramientas de este servidor en <see cref="McpClient.GetToolsAsync"/>
    /// (p. ej. <c>"github_"</c>) cuando no se pasa uno explícito. Evita colisiones entre servidores.
    /// </summary>
    public string? ToolNamePrefix { get; init; }

    /// <summary>Qué hacer si la definición de una herramienta cambia después de fijarla. Por defecto, <see cref="McpToolChangePolicy.Reject"/>.</summary>
    public McpToolChangePolicy ToolChangePolicy { get; init; } = McpToolChangePolicy.Reject;

    /// <summary>
    /// Hashes de definiciones aprobadas en sesiones anteriores (nombre → hash, ver <see cref="McpClient.ToolHashes"/>).
    /// Permiten detectar cambios entre conexiones, no solo dentro de una.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PinnedToolHashes { get; init; }

    /// <summary>Páginas máximas que se recorren en los listados paginados (<c>tools/list</c>, <c>resources/list</c>...). Por defecto, 100.</summary>
    public int MaxListPages { get; init; } = 100;
}

/// <summary>Información del servidor devuelta en <c>initialize</c>.</summary>
public sealed record McpServerInfo(string Name, string? Version);

/// <summary>Herramienta publicada por un servidor MCP.</summary>
public sealed record McpToolInfo(string Name, string? Description, JsonElement InputSchema)
{
    public string? Title { get; init; }

    /// <summary>SHA-256 (hex) de la definición canónica: nombre, título, descripción, esquema de entrada y anotaciones.</summary>
    public string DefinitionHash { get; init; } = string.Empty;

    /// <summary>True si la definición difiere de la fijada y <see cref="McpClientOptions.ToolChangePolicy"/> es <see cref="McpToolChangePolicy.Reject"/>.</summary>
    public bool DefinitionChanged { get; init; }
}

/// <summary>Resultado de <c>tools/call</c>.</summary>
public sealed record McpCallToolResult(JsonArray Content, bool IsError, JsonNode? StructuredContent)
{
    /// <summary>Texto concatenado de los bloques <c>text</c>.</summary>
    public string Text => string.Join("\n", Content.OfType<JsonObject>().Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]));
}

/// <summary>Error JSON-RPC devuelto por el servidor MCP.</summary>
public class McpException(string message, int code = 0) : AiSdkException(message)
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

    /// <summary>Instrucciones opcionales del servidor (pueden añadirse al prompt de sistema).</summary>
    public string? Instructions { get; private set; }

    public string ProtocolVersion { get; private set; } = LatestProtocolVersion;

    /// <summary>
    /// Hashes fijados de las definiciones de herramientas (nombre → SHA-256). Guárdelos y páselos en
    /// <see cref="McpClientOptions.PinnedToolHashes"/> para detectar cambios entre sesiones.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolHashes => new Dictionary<string, string>(_toolHashes, StringComparer.Ordinal);

    /// <summary>Se invoca con cada notificación del servidor (método y parámetros).</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Se invoca cuando una herramienta ya fijada aparece con otra definición (posible "rug pull").</summary>
    public event Action<McpToolDefinitionChange>? ToolDefinitionChanged;

    /// <summary>
    /// Se invoca cuando falla la atención de una petición del servidor (método y excepción). Al servidor solo se le
    /// envía un mensaje genérico: aquí está el detalle para el registro del host.
    /// </summary>
    public event Action<string, Exception>? ServerRequestFailed;

    /// <summary>Conecta e inicializa la sesión MCP.</summary>
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
    /// Lista todas las herramientas del servidor (recorre la paginación). Cada definición se fija por su hash la
    /// primera vez que aparece; si cambia en un listado posterior se aplica <see cref="McpClientOptions.ToolChangePolicy"/>.
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

    /// <summary>Fija la definición o la compara con la fijada. Devuelve true si la herramienta queda bloqueada.</summary>
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

    /// <summary>SHA-256 de la forma canónica (claves ordenadas) de lo que ve el modelo y las anotaciones.</summary>
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
    /// Invoca una herramienta del servidor. Se rechaza si su definición cambió después de fijarla y la política es
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
    /// Devuelve las herramientas del servidor como <see cref="Tool"/> de AiSdk. Omite las bloqueadas por un cambio de
    /// definición y, con <see cref="McpClientOptions.RequireToolApproval"/>, todas requieren aprobación humana.
    /// </summary>
    /// <param name="include">Si se indica, solo se incluyen estas herramientas.</param>
    /// <param name="prefix">Prefijo para evitar colisiones de nombre entre servidores (p. ej. <c>"github_"</c>); por defecto, <see cref="McpClientOptions.ToolNamePrefix"/>.</param>
    /// <param name="existingTools">Herramientas con las que se combinarán (locales u otros servidores): si un nombre coincide se lanza <see cref="McpException"/>.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
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
    [Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
    public Task SetRootsAsync(IReadOnlyList<McpRoot> roots, CancellationToken cancellationToken = default)
    {
        _roots = roots;
        return SendNotificationAsync("notifications/roots/list_changed", null, cancellationToken);
    }

    /// <summary>
    /// Recorre un listado paginado. Falla si el servidor repite un cursor (bucle) o si se supera
    /// <see cref="McpClientOptions.MaxListPages"/>.
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
    /// Envía una petición JSON-RPC arbitraria y devuelve su <c>result</c>. <see cref="McpClientOptions.RequestTimeout"/>
    /// cubre el envío, la lectura del cuerpo y la espera de la respuesta.
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

    /// <summary>Cierra la conexión. Nunca lanza excepciones (también cierra la sesión HTTP aunque un stream haya fallado).</summary>
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
