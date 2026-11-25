using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiSdk.Http;

namespace AiSdk.Mcp;

/// <summary>Transport for JSON-RPC messages between the client and an MCP server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Messages received from the server (responses, requests and notifications).</summary>
    ChannelReader<JsonObject> Messages { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task SendAsync(JsonObject message, CancellationToken cancellationToken = default);

    /// <summary>Called by the client after <c>initialize</c> with the negotiated protocol version.</summary>
    void SetProtocolVersion(string version)
    {
    }
}

/// <summary>
/// stdio transport: launches the server as a process and communicates over stdin/stdout
/// using newline-delimited JSON (e.g. <c>npx -y @modelcontextprotocol/server-filesystem</c>).
/// </summary>
/// <remarks>
/// The child process does <b>not</b> inherit the host's full environment: it only receives the variables in
/// <see cref="DefaultInheritedVariables"/>, those in <see cref="InheritedVariables"/> and those passed
/// explicitly to the constructor, so a third-party server never sees API keys or connection strings.
/// Set <see cref="InheritEnvironment"/> = true only for trusted servers.
/// </remarks>
public sealed class StdioMcpTransport : IMcpTransport
{
    // Cola acotada: si el cliente no consume, la lectura se detiene y el servidor queda bloqueado en su stdout.
    private readonly Channel<JsonObject> _messages = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ProcessStartInfo? _startInfo;
    private readonly IReadOnlyDictionary<string, string> _environment = new Dictionary<string, string>();
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private Task? _readLoop;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Maximum size of a JSON-RPC message (one stdout line), in bytes: 16 MiB. Larger messages are discarded.</summary>
    public long MaxMessageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum length of a stderr line delivered to <c>OnStderr</c>, in bytes (64 KiB); the rest is truncated.</summary>
    public int MaxStderrLineBytes { get; init; } = 64 * 1024;

    /// <summary>Launches <paramref name="command"/> with the specified arguments and environment variables.</summary>
    /// <param name="command">Server executable (launched without a shell).</param>
    /// <param name="arguments">Arguments, passed one by one without interpretation.</param>
    /// <param name="environment">Variables passed to the server in addition to the allowed inherited ones.</param>
    /// <param name="workingDirectory">Working directory (defaults to the current one).</param>
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
    /// Host variables the server inherits by default (the same as the official MCP TypeScript SDK):
    /// <c>HOME, LOGNAME, PATH, SHELL, TERM, USER</c> on POSIX and
    /// <c>APPDATA, HOMEDRIVE, HOMEPATH, LOCALAPPDATA, PATH, PROCESSOR_ARCHITECTURE, SYSTEMDRIVE, SYSTEMROOT, TEMP, USERNAME, USERPROFILE</c> on Windows.
    /// </summary>
    public static IReadOnlyList<string> DefaultInheritedVariables { get; } = OperatingSystem.IsWindows()
        ? ["APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PROCESSOR_ARCHITECTURE", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "USERNAME", "USERPROFILE"]
        : ["HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER"];

    /// <summary>
    /// Additional host variables the server may inherit (e.g. <c>HTTPS_PROXY</c> or <c>NODE_EXTRA_CA_CERTS</c>).
    /// They are added to <see cref="DefaultInheritedVariables"/>.
    /// </summary>
    public IReadOnlyCollection<string>? InheritedVariables { get; init; }

    /// <summary>
    /// If true, the server inherits the host's <b>entire</b> environment (the behavior before 0.2):
    /// API keys, connection strings and any secret in environment variables. Use it only with trusted servers.
    /// </summary>
    public bool InheritEnvironment { get; init; }

    /// <summary>Uses already open streams (useful for tests or in-process servers).</summary>
    /// <param name="serverInput">Stream to which messages for the server are written.</param>
    /// <param name="serverOutput">Stream from which the server's messages are read.</param>
    public static StdioMcpTransport FromStreams(Stream serverInput, Stream serverOutput) => new(serverInput, serverOutput);

    /// <summary>Receives each line the server writes to stderr (logs).</summary>
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
            var stderr = new BoundedLineReader(_process.StandardError.BaseStream, MaxStderrLineBytes);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await stderr.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                    {
                        OnStderr?.Invoke(line.Text);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // El proceso terminó.
                }
            }, CancellationToken.None);
        }

        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Keeps only the allowed variables plus the explicit ones in the child process environment.</summary>
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
            var reader = new BoundedLineReader(_output!, MaxMessageBytes);
            while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } read)
            {
                if (read.Truncated)
                {
                    // Un mensaje más grande que el límite no se acumula en memoria: se descarta y se informa.
                    OnStderr?.Invoke($"Mensaje del servidor MCP descartado: supera el límite de {MaxMessageBytes} bytes.");
                    continue;
                }

                var line = read.Text;
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
/// MCP <b>Streamable HTTP</b> transport: each message is sent via POST and the response
/// arrives as JSON or as an SSE stream. Manages the <c>Mcp-Session-Id</c> header.
/// </summary>
/// <remarks>
/// JSON responses are read with a size limit (<see cref="MaxMessageBytes"/>) and within the deadline of the
/// cancellation token passed to <see cref="SendAsync"/> (the <see cref="McpClient"/> applies its
/// <see cref="McpClientOptions.RequestTimeout"/>, which also covers reading the body). An SSE stream is
/// closed once the response to its request arrives; if it is cut off or carries invalid data before that, the
/// pending request fails immediately with an error instead of waiting for the deadline.
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

    /// <summary>Session identifier assigned by the server (if it uses sessions).</summary>
    public string? SessionId { get; private set; }

    /// <summary>
    /// Maximum size (in bytes) of a JSON response or an SSE event from the server. Anything larger is rejected
    /// and the request fails. Defaults to 16 MiB.
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

    /// <summary>Reads the whole body without exceeding <paramref name="maxBytes"/>.</summary>
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

    /// <summary>Reads at most the first characters of an error body (for the exception message).</summary>
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

    /// <summary>Closes the open streams and the session (DELETE). Never throws.</summary>
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

/// <summary>
/// UTF-8 line reader with a maximum size: it never buffers more than <c>maxBytes</c> per line (the excess is discarded
/// up to the line break and the line is flagged as truncated). Reads in blocks, not byte by byte.
/// </summary>
internal sealed class BoundedLineReader(Stream stream, long maxBytes)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>The next line (without a trailing <c>\n</c> or <c>\r</c>); null at the end of the stream.</summary>
    public async Task<(string Text, bool Truncated)?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var truncated = false;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    return line.Length > 0 || truncated ? (Decode(line), truncated) : null;
                }
            }

            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf((byte)'\n');
            var chunk = newline >= 0 ? available[..newline] : available;
            var room = (int)Math.Min(chunk.Length, Math.Max(0, maxBytes - line.Length));
            line.Write(chunk[..room]);
            truncated |= room < chunk.Length;
            _start += newline >= 0 ? newline + 1 : available.Length;
            if (newline >= 0)
            {
                return (Decode(line), truncated);
            }
        }
    }

    private static string Decode(MemoryStream bytes)
    {
        var span = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        return Encoding.UTF8.GetString(span.EndsWith("\r"u8) ? span[..^1] : span);
    }
}
