using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AiSdk.Tests;

/// <summary>Petición capturada por <see cref="FakeHandler"/>.</summary>
internal sealed record CapturedRequest(HttpRequestMessage Request, JsonObject Body, string Raw)
{
    public void Deconstruct(out HttpRequestMessage request, out JsonObject body)
    {
        request = Request;
        body = Body;
    }
}

/// <summary>Handler HTTP falso que captura peticiones y devuelve respuestas predefinidas.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public FakeHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK, IDictionary<string, string>? headers = null) =>
        Enqueue(status, new StringContent(body, Encoding.UTF8, "application/json"), headers);

    public FakeHandler Sse(params string[] events) =>
        Enqueue(HttpStatusCode.OK, new StringContent(string.Concat(events.Select(e => e + "\n\n")), Encoding.UTF8, "text/event-stream"), null);

    public FakeHandler Bytes(byte[] body, string contentType) =>
        Enqueue(HttpStatusCode.OK, new ByteArrayContent(body) { Headers = { ContentType = new(contentType) } }, null);

    public FakeHandler Accepted() => Enqueue(HttpStatusCode.Accepted, new ByteArrayContent([]), null);

    private FakeHandler Enqueue(HttpStatusCode status, HttpContent content, IDictionary<string, string>? headers)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = content };
            foreach (var (k, v) in headers ?? new Dictionary<string, string>())
            {
                response.Headers.TryAddWithoutValidation(k, v);
            }

            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        JsonObject body;
        try
        {
            body = request.Content?.Headers.ContentType?.MediaType == "application/json" && raw.Length > 0
                ? JsonNode.Parse(raw)!.AsObject()
                : [];
        }
        catch (System.Text.Json.JsonException)
        {
            body = [];
        }

        lock (Requests)
        {
            Requests.Add(new CapturedRequest(request, body, raw));
        }

        return _responses.Dequeue()();
    }
}
