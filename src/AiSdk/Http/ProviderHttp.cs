using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiSdk.Http;

/// <summary>Evento Server-Sent Events.</summary>
public readonly record struct SseEvent(string? Event, string Data)
{
    /// <summary>
    /// True si el stream terminó sin la línea en blanco que cierra el evento (conexión cortada a mitad del evento,
    /// o un servidor que no la envía al final): sus datos pueden estar truncados.
    /// </summary>
    public bool Incomplete { get; init; }
}

/// <summary>Límites al leer un stream SSE (ver <see cref="ProviderHttp.ReadSseAsync(HttpResponseMessage, SseReadOptions, CancellationToken)"/>).</summary>
public sealed record SseReadOptions
{
    /// <summary>Caracteres máximos de una línea (16 M por defecto). Una línea mayor detiene el stream con un error.</summary>
    public int MaxLineChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>Caracteres máximos de los datos de un evento (16 M por defecto).</summary>
    public int MaxEventChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Tiempo máximo sin recibir datos una vez llegadas las cabeceras (null = sin límite). Al superarse se lanza
    /// <see cref="TimeoutException"/> (que se reintenta si aún no se emitió nada).
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>Límites por defecto, sin tiempo de inactividad.</summary>
    public static SseReadOptions Default { get; } = new();
}

/// <summary>Utilidades HTTP compartidas por los proveedores.</summary>
public static class ProviderHttp
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(CreateDefaultHandler()) { Timeout = TimeSpan.FromMinutes(10) });

    /// <summary>
    /// Cliente compartido por los proveedores cuando no se indica uno. Renueva las conexiones cada 5 minutos (para
    /// respetar cambios de DNS) y sigue redirecciones con <see cref="SafeRedirectHandler"/>: las claves de API no se
    /// reenvían a otro origen.
    /// </summary>
    public static HttpClient DefaultClient => SharedClient.Value;

    /// <summary>
    /// Límites con que los modelos de los proveedores leen sus streams: 16 M caracteres por línea/evento y
    /// 10 minutos de inactividad como máximo (sin él, un servidor que deja de enviar datos colgaría la llamada).
    /// </summary>
    public static SseReadOptions ModelStreamOptions { get; set; } = new() { IdleTimeout = TimeSpan.FromMinutes(10) };

    /// <summary>Handler recomendado para clientes de proveedores: sin redirecciones automáticas inseguras y con conexiones renovadas.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SafeRedirectHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    /// <summary>Resuelve una API key a partir del valor explícito o de una variable de entorno.</summary>
    public static string LoadApiKey(string? apiKey, string environmentVariable, string providerName) =>
        !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : Environment.GetEnvironmentVariable(environmentVariable)
              ?? throw new AiSdkException($"Falta la API key de {providerName}. Pásala en las opciones o define la variable de entorno {environmentVariable}.");

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

    /// <summary>Crea una petición sin cuerpo (GET, DELETE...) con las cabeceras indicadas.</summary>
    public static HttpRequestMessage CreateRequest(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    /// <summary>Envía una petición y devuelve el JSON de la respuesta (lanza <see cref="ApiCallException"/> si falla).</summary>
    public static async Task<JsonObject> SendJsonAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (var response = await SendAsync(client, request, stream: false, cancellationToken).ConfigureAwait(false))
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        }
    }

    /// <summary>Lee un cuerpo JSONL (una línea JSON por elemento).</summary>
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

    /// <summary>Contenido multipart con un archivo y campos de texto.</summary>
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

    /// <summary>Envía la petición y lanza <see cref="ApiCallException"/> si la respuesta no es exitosa.</summary>
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
               ?? throw new AiSdkException("Respuesta JSON inválida del proveedor.");
    }

    /// <summary>Lee un stream SSE evento por evento, con los límites de <see cref="SseReadOptions.Default"/>.</summary>
    public static IAsyncEnumerable<SseEvent> ReadSseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ReadSseAsync(response, SseReadOptions.Default, cancellationToken);

    /// <summary>
    /// Lee un stream SSE evento por evento. Ignora comentarios (<c>:</c>, keep-alive), limita el tamaño de líneas y
    /// eventos, y con <see cref="SseReadOptions.IdleTimeout"/> lanza <see cref="TimeoutException"/> si el servidor deja
    /// de enviar datos. Un evento sin la línea en blanco final se entrega con <see cref="SseEvent.Incomplete"/>.
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
                        throw new AiSdkException($"Evento SSE demasiado grande (más de {options.MaxEventChars} caracteres).");
                    }

                    data.Append(value);
                    hasData = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Interpreta los datos de un evento SSE de un modelo como objeto JSON. Devuelve null (el evento se ignora) si no
    /// son JSON, como <c>[DONE]</c> o los keep-alive de texto ("ping") de algunos servidores compatibles, o si es el
    /// último evento cortado al cerrarse la conexión. Un evento completo con JSON inválido lanza
    /// <see cref="AiSdkException"/> con un mensaje claro.
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
            throw new AiSdkException($"El stream de {providerName} envió un evento con JSON inválido: {snippet}", ex);
        }
    }

    /// <summary>Lector de líneas SSE (CR, LF o CRLF) con tamaño máximo y tiempo de inactividad.</summary>
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
                throw new AiSdkException($"Línea SSE demasiado grande (más de {options.MaxLineChars} caracteres).");
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
                throw new TimeoutException($"El stream no envió datos en {timeout.TotalSeconds:0} s.");
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
/// Sigue redirecciones de forma segura para llamadas autenticadas: solo en GET y HEAD, como máximo
/// <see cref="MaxRedirects"/>, nunca de HTTPS a HTTP y, si cambia el origen (esquema, host o puerto), sin reenviar
/// ninguna cabecera salvo <c>Accept</c>, <c>Accept-Language</c> y <c>User-Agent</c>: .NET solo quita
/// <c>Authorization</c> al redirigir, no cabeceras propias como <c>x-api-key</c> o <c>x-goog-api-key</c>.
/// El resto de las redirecciones (p. ej. en un POST) se devuelve tal cual y el proveedor la informa como error.
/// </summary>
/// <remarks>El handler interno debe tener las redirecciones automáticas desactivadas (<c>AllowAutoRedirect = false</c>).</remarks>
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

    /// <summary>Redirecciones máximas que se siguen (5 por defecto).</summary>
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
