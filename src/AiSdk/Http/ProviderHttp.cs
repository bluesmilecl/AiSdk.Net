using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace AiSdk.Http;

/// <summary>Evento Server-Sent Events.</summary>
public readonly record struct SseEvent(string? Event, string Data);

/// <summary>Utilidades HTTP compartidas por los proveedores.</summary>
public static class ProviderHttp
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    public static HttpClient DefaultClient => SharedClient.Value;

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
        var retryAfter = response.Headers.RetryAfter?.Delta
                         ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
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

    /// <summary>Lee un stream SSE evento por evento.</summary>
    public static async IAsyncEnumerable<SseEvent> ReadSseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
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
