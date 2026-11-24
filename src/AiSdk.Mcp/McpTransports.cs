using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiSdk.Http;

namespace AiSdk.Mcp;

/// <summary>Transporte de mensajes JSON-RPC entre el cliente y un servidor MCP.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Mensajes recibidos del servidor (respuestas, peticiones y notificaciones).</summary>
    ChannelReader<JsonObject> Messages { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task SendAsync(JsonObject message, CancellationToken cancellationToken = default);

    /// <summary>Lo invoca el cliente tras <c>initialize</c> con la versión de protocolo negociada.</summary>
    void SetProtocolVersion(string version)
    {
    }
}

/// <summary>
/// Transporte stdio: lanza el servidor como proceso y se comunica por stdin/stdout
/// con JSON delimitado por saltos de línea (p. ej. <c>npx -y @modelcontextprotocol/server-filesystem</c>).
/// </summary>
/// <remarks>
/// El proceso hijo <b>no</b> hereda el entorno completo del host: solo recibe las variables de
/// <see cref="DefaultInheritedVariables"/>, las de <see cref="InheritedVariables"/> y las pasadas
/// explícitamente en el constructor. Así un servidor de terceros no ve API keys ni cadenas de conexión.
/// Use <see cref="InheritEnvironment"/> = true solo con servidores de confianza.
/// </remarks>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ProcessStartInfo? _startInfo;
    private readonly IReadOnlyDictionary<string, string> _environment = new Dictionary<string, string>();
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private Task? _readLoop;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Lanza <paramref name="command"/> con los argumentos y variables de entorno indicados.</summary>
    /// <param name="command">Ejecutable del servidor (se lanza sin shell).</param>
    /// <param name="arguments">Argumentos, pasados uno a uno sin interpretar.</param>
    /// <param name="environment">Variables que se pasan al servidor además de las heredadas permitidas.</param>
    /// <param name="workingDirectory">Directorio de trabajo (por defecto, el actual).</param>
    public StdioMcpTransport(
        string command,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        _startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in arguments ?? [])
        {
            _startInfo.ArgumentList.Add(arg);
        }

        _environment = environment ?? new Dictionary<string, string>();
    }

    private StdioMcpTransport(Stream serverInput, Stream serverOutput)
    {
        _input = serverInput;
        _output = serverOutput;
    }

    /// <summary>
    /// Variables del host que el servidor hereda por defecto (las mismas que el SDK oficial de MCP en TypeScript):
    /// <c>HOME, LOGNAME, PATH, SHELL, TERM, USER</c> en POSIX y
    /// <c>APPDATA, HOMEDRIVE, HOMEPATH, LOCALAPPDATA, PATH, PROCESSOR_ARCHITECTURE, SYSTEMDRIVE, SYSTEMROOT, TEMP, USERNAME, USERPROFILE</c> en Windows.
    /// </summary>
    public static IReadOnlyList<string> DefaultInheritedVariables { get; } = OperatingSystem.IsWindows()
        ? ["APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PROCESSOR_ARCHITECTURE", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "USERNAME", "USERPROFILE"]
        : ["HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER"];

    /// <summary>
    /// Variables adicionales del host que el servidor puede heredar (p. ej. <c>HTTPS_PROXY</c> o <c>NODE_EXTRA_CA_CERTS</c>).
    /// Se suman a <see cref="DefaultInheritedVariables"/>.
    /// </summary>
    public IReadOnlyCollection<string>? InheritedVariables { get; init; }

    /// <summary>
    /// Si es true, el servidor hereda <b>todo</b> el entorno del host (comportamiento anterior a 0.2):
    /// API keys, cadenas de conexión y cualquier secreto en variables de entorno. Úselo solo con servidores de confianza.
    /// </summary>
    public bool InheritEnvironment { get; init; }

    /// <summary>Usa streams ya abiertos (útil para pruebas o servidores en el mismo proceso).</summary>
    /// <param name="serverInput">Stream donde se escriben los mensajes hacia el servidor.</param>
    /// <param name="serverOutput">Stream desde donde se leen los mensajes del servidor.</param>
    public static StdioMcpTransport FromStreams(Stream serverInput, Stream serverOutput) => new(serverInput, serverOutput);

    /// <summary>Recibe cada línea que el servidor escribe en stderr (logs).</summary>
    public Action<string>? OnStderr { get; init; }

    public ChannelReader<JsonObject> Messages => _messages.Reader;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_startInfo is not null)
        {
            ApplyEnvironment(_startInfo);
            _process = Process.Start(_startInfo) ?? throw new AiSdkException($"No se pudo iniciar el servidor MCP '{_startInfo.FileName}'.");
            _input = _process.StandardInput.BaseStream;
            _output = _process.StandardOutput.BaseStream;
            _ = Task.Run(async () =>
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    OnStderr?.Invoke(line);
                }
            }, CancellationToken.None);
        }

        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Deja en el entorno del proceso hijo solo las variables permitidas más las explícitas.</summary>
    private void ApplyEnvironment(ProcessStartInfo startInfo)
    {
        // ProcessStartInfo.Environment empieza como una copia del entorno del proceso actual.
        var environment = startInfo.Environment;
        if (!InheritEnvironment)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var allowed = new HashSet<string>(DefaultInheritedVariables, comparer);
            allowed.UnionWith(InheritedVariables ?? []);
            foreach (var key in environment.Keys.ToList())
            {
                // Las funciones exportadas de bash ("() { ...") se descartan siempre, como en el SDK de TypeScript.
                if (!allowed.Contains(key) || environment[key] is { } value && value.StartsWith("()", StringComparison.Ordinal))
                {
                    environment.Remove(key);
                }
            }
        }

        foreach (var (key, value) in _environment)
        {
            environment[key] = value;
        }
    }

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_output!, Encoding.UTF8);
            while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    OnStderr?.Invoke(line); // Línea que no es JSON-RPC (logs mal dirigidos a stdout).
                    continue;
                }

                if (node is JsonObject obj)
                {
                    await _messages.Writer.WriteAsync(obj, _cts.Token).ConfigureAwait(false);
                }
                else if (node is JsonArray batch)
                {
                    foreach (var item in batch.OfType<JsonObject>())
                    {
                        await _messages.Writer.WriteAsync((JsonObject)item.DeepClone(), _cts.Token).ConfigureAwait(false);
                    }
                }
            }

            _messages.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _messages.Writer.TryComplete(ex is OperationCanceledException ? null : ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _input?.Close();
        }
        catch (IOException)
        {
        }

        if (_process is not null)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.Dispose();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// Transporte <b>Streamable HTTP</b> de MCP: cada mensaje se envía por POST y la respuesta
/// llega como JSON o como stream SSE. Gestiona la cabecera <c>Mcp-Session-Id</c>.
/// </summary>
/// <remarks>
/// Las respuestas JSON se leen con un límite de tamaño (<see cref="MaxMessageBytes"/>) y dentro del plazo del
/// token de cancelación de <see cref="SendAsync"/> (el <see cref="McpClient"/> le aplica su
/// <see cref="McpClientOptions.RequestTimeout"/>, que cubre también la lectura del cuerpo). Un stream SSE se
/// cierra al llegar la respuesta a su petición; si se corta o trae datos inválidos antes, la petición
/// pendiente falla de inmediato con un error en vez de esperar al plazo.
/// </remarks>
public sealed class HttpMcpTransport : IMcpTransport
{
    private const int MaxErrorBodyChars = 4096;

    private readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly List<Task> _pendingStreams = [];
    private readonly CancellationTokenSource _cts = new();
    private string? _protocolVersion;
    private int _disposed;

    public HttpMcpTransport(Uri endpoint, HttpClient? httpClient = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        _endpoint = endpoint;
        _http = httpClient ?? ProviderHttp.DefaultClient;
        _headers = headers ?? new Dictionary<string, string>();
    }

    public HttpMcpTransport(string endpoint, IReadOnlyDictionary<string, string>? headers = null)
        : this(new Uri(endpoint), null, headers)
    {
    }

    /// <summary>Identificador de sesión asignado por el servidor (si usa sesiones).</summary>
    public string? SessionId { get; private set; }

    /// <summary>
    /// Tamaño máximo (en bytes) de una respuesta JSON o de un evento SSE del servidor. Lo que lo supere se rechaza
    /// y la petición falla. Por defecto, 16 MiB.
    /// </summary>
    public long MaxMessageBytes { get; init; } = 16 * 1024 * 1024;

    public ChannelReader<JsonObject> Messages => _messages.Reader;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void SetProtocolVersion(string version) => _protocolVersion = version;

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        // Solo las peticiones (method + id) esperan respuesta; se recuerda el id para cerrar su stream SSE al responder.
        var requestId = message["method"] is not null ? message["id"]?.DeepClone() : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddHeaders(request);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var ownedByReader = false;
        try
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var session))
            {
                SessionId = session.FirstOrDefault();
            }

            if (response.StatusCode == HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
            {
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var body = await ReadErrorBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                throw new ApiCallException($"El servidor MCP respondió {status}: {body}", status, body, _endpoint);
            }

            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                // El stream SSE puede contener peticiones del servidor antes de la respuesta: se lee en segundo plano.
                // Se enlaza con el token de la petición (que lleva el plazo del cliente) y con el cierre del transporte.
                var streamToken = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
                var task = Task.Run(() => ReadStreamAsync(response, requestId, streamToken), CancellationToken.None);
                ownedByReader = true;
                lock (_pendingStreams)
                {
                    _pendingStreams.RemoveAll(t => t.IsCompleted);
                    _pendingStreams.Add(task);
                }

                return;
            }

            var bytes = await ReadBoundedAsync(response.Content, MaxMessageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var item in Parse(Encoding.UTF8.GetString(bytes)))
            {
                await _messages.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!ownedByReader)
            {
                response.Dispose();
            }
        }
    }

    private async Task ReadStreamAsync(HttpResponseMessage response, JsonNode? requestId, CancellationTokenSource streamToken)
    {
        var responded = false;
        Exception? failure = null;
        try
        {
            await foreach (var sse in ProviderHttp.ReadSseAsync(response, streamToken.Token).ConfigureAwait(false))
            {
                if (Encoding.UTF8.GetByteCount(sse.Data) > MaxMessageBytes)
                {
                    throw new McpException($"Un evento SSE del servidor MCP supera el límite de {MaxMessageBytes} bytes.");
                }

                foreach (var item in Parse(sse.Data))
                {
                    await _messages.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                    responded |= requestId is not null && item["method"] is null && JsonNode.DeepEquals(item["id"], requestId);
                }

                if (responded)
                {
                    break; // La respuesta ya llegó: el stream de esta petición no necesita seguir abierto.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre del transporte o plazo de la petición vencido: el cliente ya informa el error.
        }
        catch (Exception ex)
        {
            // JSON inválido, evento demasiado grande o conexión cortada: nunca debe escapar de la tarea en segundo plano.
            failure = ex;
        }
        finally
        {
            response.Dispose();
            streamToken.Dispose();
        }

        if (!responded && requestId is not null && !_cts.IsCancellationRequested)
        {
            // La petición pendiente falla de inmediato en vez de esperar al plazo.
            var reason = failure is McpException ? failure.Message : "El stream SSE del servidor MCP terminó sin respuesta.";
            _messages.Writer.TryWrite(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = failure is null || failure is McpException ? reason : $"{reason} ({failure.GetType().Name})" },
            });
        }
    }

    private static List<JsonObject> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new McpException($"El servidor MCP envió JSON inválido: {ex.Message}");
        }

        return node switch
        {
            JsonObject obj => [obj],
            JsonArray batch => batch.OfType<JsonObject>().Select(i => (JsonObject)i.DeepClone()).ToList(),
            _ => [],
        };
    }

    /// <summary>Lee el cuerpo completo sin superar <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new McpException($"La respuesta del servidor MCP supera el límite de {maxBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new McpException($"La respuesta del servidor MCP supera el límite de {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Lee como mucho los primeros caracteres de un cuerpo de error (para el mensaje de la excepción).</summary>
    private static async Task<string> ReadErrorBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[MaxErrorBodyChars];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return new string(buffer, 0, total);
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        if (SessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);
        }

        if (_protocolVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        }

        foreach (var (key, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>Cierra los streams abiertos y la sesión (DELETE). Nunca lanza excepciones.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_pendingStreams)
        {
            pending = [.. _pendingStreams];
        }

        try
        {
            // Los lectores no lanzan, pero el cierre de sesión no puede depender de ellos.
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (SessionId is not null)
        {
            // Cierre de sesión best-effort.
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint);
                AddHeaders(request);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var _ = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _messages.Writer.TryComplete();
        _cts.Dispose();
    }
}
