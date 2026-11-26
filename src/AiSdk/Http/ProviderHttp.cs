using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Resources;
using System.Globalization;

namespace AiSdk.Http;

/// <summary>Server-Sent Events event.</summary>
public readonly record struct SseEvent(string? Event, string Data)
{
    /// <summary>
    /// True if the stream ended without the blank line that closes the event (connection dropped mid-event,
    /// or a server that does not send it at the end): its data may be truncated.
    /// </summary>
    public bool Incomplete { get; init; }
}

/// <summary>Limits for reading an SSE stream (see <see cref="ProviderHttp.ReadSseAsync(HttpResponseMessage, SseReadOptions, CancellationToken)"/>).</summary>
public sealed record SseReadOptions
{
    /// <summary>
    /// Gets or sets the maximum characters in a line (16 M by default). A longer line stops the stream with an error.
    /// </summary>
    public int MaxLineChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the maximum characters in an event's data (16 M by default).</summary>
    public int MaxEventChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum time without receiving data once the headers have arrived (null = no limit). When
    /// exceeded, a <see cref="TimeoutException"/> is thrown (which is retried if nothing has been emitted yet).
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>Gets the default limits, without an idle timeout.</summary>
    public static SseReadOptions Default { get; } = new();
}

/// <summary>HTTP utilities shared by the providers.</summary>
public static class ProviderHttp
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(CreateDefaultHandler()) { Timeout = TimeSpan.FromMinutes(10) });

    /// <summary>
    /// Gets the client shared by the providers when none is specified. It renews connections every 5 minutes (to
    /// honor DNS changes) and follows redirects with <see cref="SafeRedirectHandler"/>: API keys are not
    /// forwarded to another origin.
    /// </summary>
    public static HttpClient DefaultClient => SharedClient.Value;

    /// <summary>
    /// Gets or sets the limits with which provider models read their streams: 16 M characters per line/event and
    /// at most 10 minutes of inactivity (without it, a server that stops sending data would hang the call).
    /// </summary>
    public static SseReadOptions ModelStreamOptions { get; set; } = new() { IdleTimeout = TimeSpan.FromMinutes(10) };

    /// <summary>Creates the recommended handler for provider clients: no unsafe automatic redirects, and renewed connections.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SafeRedirectHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    /// <summary>Resolves an API key from the explicit value or from an environment variable.</summary>
    public static string LoadApiKey(string? apiKey, string environmentVariable, string providerName) =>
        !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : Environment.GetEnvironmentVariable(environmentVariable)
              ?? throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Strings.Http_MissingApiKey, providerName, environmentVariable));

    public static HttpRequestMessage CreateJsonRequest(Uri url, JsonObject body, IEnumerable<KeyValuePair<string, string>> headers, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        return request;
    }

    /// <summary>Creates a request without a body (GET, DELETE...) with the given headers.</summary>
    public static HttpRequestMessage CreateRequest(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    /// <summary>Sends a request and returns the response JSON (throws <see cref="ApiCallException"/> on failure).</summary>
    public static async Task<JsonObject> SendJsonAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await SendAsync(client, request, stream: false, cancellationToken).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        }
    }

    /// <summary>Reads a JSONL body (one JSON line per element).</summary>
    public static async IAsyncEnumerable<JsonObject> ReadJsonLinesAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line) && JsonNode.Parse(line) is JsonObject obj)
            {
                yield return obj;
            }
        }
    }

    /// <summary>Creates multipart content with a file and text fields.</summary>
    public static MultipartFormDataContent FileForm(byte[] data, string filename, string mediaType, IEnumerable<KeyValuePair<string, string>>? fields = null)
    {
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? [])
        {
            form.Add(new StringContent(value), key);
        }

        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", filename);
        return form;
    }

    public static DateTimeOffset? ParseTimestamp(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var unix) => DateTimeOffset.FromUnixTimeSeconds(unix),
        JsonValue v when v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) => date,
        _ => null,
    };

    /// <summary>
    /// Sends the request and throws <see cref="ApiCallException"/> if the response is not successful.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, bool stream, CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(
            request,
            stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // Una fecha ya pasada (relojes desfasados) equivale a "reintentar ya": nunca un retraso negativo.
        var retryAfter = response.Headers.RetryAfter?.Delta
                         ?? (response.Headers.RetryAfter?.Date is { } date ? TimeSpan.FromTicks(Math.Max(0, (date - DateTimeOffset.UtcNow).Ticks)) : null);
        var message = TryExtractErrorMessage(body) ?? response.ReasonPhrase ?? "Error";
        response.Dispose();
        throw new ApiCallException($"{(int)response.StatusCode}: {message}", (int)response.StatusCode, body, request.RequestUri, retryAfter);
    }

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)) as JsonObject
               ?? throw new AiSdkException(Strings.Http_InvalidJsonResponse);
    }

    /// <summary>Reads an SSE stream event by event, with the limits of <see cref="SseReadOptions.Default"/>.</summary>
    public static IAsyncEnumerable<SseEvent> ReadSseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ReadSseAsync(response, SseReadOptions.Default, cancellationToken);

    /// <summary>
    /// Reads an SSE stream event by event. Ignores comments (<c>:</c>, keep-alive), limits the size of lines and
    /// events, and with <see cref="SseReadOptions.IdleTimeout"/> throws <see cref="TimeoutException"/> if the server stops
    /// sending data. An event without the final blank line is delivered with <see cref="SseEvent.Incomplete"/>.
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadSseAsync(
        HttpResponseMessage response,
        SseReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lines = new SseLineReader(reader, options, idle, cancellationToken);
        string? eventName = null;
        var data = new StringBuilder();
        var hasData = false;
        while (true)
        {
            var line = await lines.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                if (hasData)
                {
                    yield return new SseEvent(eventName, data.ToString()) { Incomplete = true };
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    if (data.Length + value.Length > options.MaxEventChars)
                    {
                        throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Strings.Http_SseEventTooLarge, options.MaxEventChars));
                    }

                    data.Append(value);
                    hasData = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Parses the data of a model's SSE event as a JSON object. Returns null (the event is ignored) if it is not
    /// JSON, such as <c>[DONE]</c> or the text keep-alives ("ping") of some compatible servers, or if it is the
    /// last event, cut off when the connection closed. A complete event with invalid JSON throws
    /// <see cref="AiSdkException"/> with a clear message.
    /// </summary>
    public static JsonObject? ParseSseJson(SseEvent sse, string providerName)
    {
        var data = sse.Data.AsSpan().Trim();
        if (data.Length == 0 || data is "[DONE]" || (data[0] != '{' && data[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(sse.Data) as JsonObject;
        }
        catch (JsonException) when (sse.Incomplete)
        {
            return null; // la conexión se cortó a mitad del último evento
        }
        catch (JsonException ex)
        {
            var snippet = sse.Data.Length > 200 ? sse.Data[..200] + "…" : sse.Data;
            throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Strings.Http_StreamInvalidJson, providerName, snippet), ex);
        }
    }

    /// <summary>SSE line reader (CR, LF or CRLF) with a maximum size and an idle timeout.</summary>
    private sealed class SseLineReader(StreamReader reader, SseReadOptions options, CancellationTokenSource idle, CancellationToken cancellationToken)
    {
        private readonly char[] _buffer = new char[8192];
        private readonly StringBuilder _line = new();
        private int _position;
        private int _length;
        private bool _skipLineFeed;

        public async ValueTask<string?> ReadLineAsync()
        {
            _line.Clear();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await FillAsync().ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0)
                    {
                        return _line.Length > 0 ? _line.ToString() : null;
                    }
                }

                var span = _buffer.AsSpan(_position, _length - _position);
                if (_skipLineFeed)
                {
                    _skipLineFeed = false;
                    if (span[0] == '\n')
                    {
                        _position++;
                        continue;
                    }
                }

                var end = span.IndexOfAny('\r', '\n');
                Append(end < 0 ? span : span[..end]);
                if (end < 0)
                {
                    _position = _length;
                    continue;
                }

                _position += end + 1;
                _skipLineFeed = span[end] == '\r';
                return _line.ToString();
            }
        }

        private void Append(ReadOnlySpan<char> chunk)
        {
            if (_line.Length + chunk.Length > options.MaxLineChars)
            {
                throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Strings.Http_SseLineTooLarge, options.MaxLineChars));
            }

            _line.Append(chunk);
        }

        private async ValueTask<int> FillAsync()
        {
            if (options.IdleTimeout is not { } timeout)
            {
                return await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            idle.CancelAfter(timeout);
            try
            {
                return await reader.ReadAsync(_buffer.AsMemory(), idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(string.Format(CultureInfo.CurrentCulture, Strings.Http_StreamIdleTimeout, timeout.TotalSeconds));
            }
        }
    }

    private static string? TryExtractErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return (string?)node?["error"]?["message"] ?? (string?)node?["message"] ?? (node?["error"] is JsonValue v ? (string?)v : null);
        }
        catch (Exception)
        {
            return body.Length > 500 ? body[..500] : body;
        }
    }

    public static string ToDataUrl(byte[] data, string mediaType) => $"data:{mediaType};base64,{Convert.ToBase64String(data)}";
}

/// <summary>
/// Follows redirects safely for authenticated calls: only for GET and HEAD, at most
/// <see cref="MaxRedirects"/>, never from HTTPS to HTTP and, if the origin (scheme, host or port) changes, without forwarding
/// any header except <c>Accept</c>, <c>Accept-Language</c> and <c>User-Agent</c>: .NET only removes
/// <c>Authorization</c> on redirect, not custom headers such as <c>x-api-key</c> or <c>x-goog-api-key</c>.
/// Any other redirect (e.g. on a POST) is returned as is, and the provider reports it as an error.
/// </summary>
/// <remarks>The inner handler must have automatic redirects disabled (<c>AllowAutoRedirect = false</c>).</remarks>
public sealed class SafeRedirectHandler : DelegatingHandler
{
    private static readonly HashSet<string> CrossOriginHeaders = new(StringComparer.OrdinalIgnoreCase) { "Accept", "Accept-Language", "User-Agent" };

    public SafeRedirectHandler()
    {
    }

    public SafeRedirectHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <summary>Gets or sets the maximum number of redirects followed (5 by default).</summary>
    public int MaxRedirects { get; init; } = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var current = request;
        for (var redirects = 0; redirects < MaxRedirects; redirects++)
        {
            if (response.StatusCode is not (System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther
                    or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect)
                || (current.Method != HttpMethod.Get && current.Method != HttpMethod.Head)
                || response.Headers.Location is not { } location
                || current.RequestUri is not { } from)
            {
                break;
            }

            var target = location.IsAbsoluteUri ? location : new Uri(from, location);
            if (target.Scheme != Uri.UriSchemeHttps && (target.Scheme != Uri.UriSchemeHttp || from.Scheme == Uri.UriSchemeHttps))
            {
                break; // nunca de HTTPS a HTTP ni a otros esquemas
            }

            var sameOrigin = string.Equals(from.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(from.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase)
                             && from.Port == target.Port;
            var next = new HttpRequestMessage(current.Method, target) { Version = current.Version, VersionPolicy = current.VersionPolicy };
            foreach (var (name, values) in current.Headers)
            {
                if (sameOrigin || CrossOriginHeaders.Contains(name))
                {
                    next.Headers.TryAddWithoutValidation(name, values);
                }
            }

            response.Dispose();
            if (!ReferenceEquals(current, request))
            {
                current.Dispose();
            }

            current = next;
            response = await base.SendAsync(next, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
