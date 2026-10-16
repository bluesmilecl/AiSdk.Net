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
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ProcessStartInfo? _startInfo;
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private Task? _readLoop;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Lanza <paramref name="command"/> con los argumentos y variables de entorno indicados.</summary>
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

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            _startInfo.Environment[key] = value;
        }
    }

    private StdioMcpTransport(Stream serverInput, Stream serverOutput)
    {
        _input = serverInput;
        _output = serverOutput;
    }

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
            _process = Process.Start(_startInfo) ?? throw new AiSdkException($"No se pudo iniciar el servidor MCP '{_startInfo.FileName}'.");
            _input = _process.StandardInput.BaseStream;
            _output = _process.StandardOutput.BaseStream;
            _ = Task.Run(async () =>
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    OnStderr?.Invoke(line);
                }
            });
        }

        _readLoop = Task.Run(ReadLoopAsync);
        return Task.CompletedTask;
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
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly List<Task> _pendingStreams = [];
    private readonly CancellationTokenSource _cts = new();
    private string? _protocolVersion;

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

    public ChannelReader<JsonObject> Messages => _messages.Reader;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void SetProtocolVersion(string version) => _protocolVersion = version;

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AddHeaders(request);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var session))
        {
            SessionId = session.FirstOrDefault();
        }

        if (response.StatusCode == HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
        {
            response.Dispose();
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ApiCallException($"El servidor MCP respondió {status}: {body}", status, body, _endpoint);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "text/event-stream")
        {
            // El stream SSE puede contener peticiones del servidor antes de la respuesta: se lee en segundo plano.
            var task = Task.Run(async () =>
            {
                using (response)
                {
                    try
                    {
                        await foreach (var sse in ProviderHttp.ReadSseAsync(response, _cts.Token).ConfigureAwait(false))
                        {
                            await PushAsync(sse.Data).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });
            lock (_pendingStreams)
            {
                _pendingStreams.RemoveAll(t => t.IsCompleted);
                _pendingStreams.Add(task);
            }

            return;
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await PushAsync(text).ConfigureAwait(false);
        }
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

    private async Task PushAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        switch (JsonNode.Parse(text))
        {
            case JsonObject obj:
                await _messages.Writer.WriteAsync(obj).ConfigureAwait(false);
                break;
            case JsonArray batch:
                foreach (var item in batch.OfType<JsonObject>())
                {
                    await _messages.Writer.WriteAsync((JsonObject)item.DeepClone()).ConfigureAwait(false);
                }

                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_pendingStreams)
        {
            pending = [.. _pendingStreams];
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

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
