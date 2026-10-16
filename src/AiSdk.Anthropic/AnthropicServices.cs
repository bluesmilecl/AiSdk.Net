using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Anthropic;

/// <summary>
/// Message Batches de Anthropic (<c>/v1/messages/batches</c>): procesa hasta 100.000 peticiones
/// de forma asíncrona a la mitad de precio. Los resultados pueden llegar en cualquier orden:
/// usa <see cref="BatchResultItem.CustomId"/> para emparejarlos.
/// </summary>
public sealed class AnthropicBatchClient : IBatchClient
{
    private readonly AnthropicProvider _provider;
    private readonly AnthropicMessagesLanguageModel _model;

    internal AnthropicBatchClient(AnthropicProvider provider, string modelId)
    {
        _provider = provider;
        _model = (AnthropicMessagesLanguageModel)provider.Messages(modelId);
    }

    public async Task<BatchJob> CreateAsync(IReadOnlyList<BatchRequestItem> requests, CancellationToken cancellationToken = default)
    {
        var betas = new HashSet<string>();
        var items = new JsonArray();
        foreach (var item in requests)
        {
            var (body, itemBetas, _) = _model.BuildBody(item.Request, stream: false);
            betas.UnionWith(itemBetas);
            items.Add(new JsonObject { ["custom_id"] = item.CustomId, ["params"] = body });
        }

        var http = ProviderHttp.CreateJsonRequest(_provider.Url("messages/batches"), new JsonObject { ["requests"] = items }, _provider.Headers(null, betas), stream: false);
        return Parse(await ProviderHttp.SendJsonAsync(_provider.Http, http, cancellationToken).ConfigureAwait(false));
    }

    public async Task<BatchJob> GetAsync(string batchId, CancellationToken cancellationToken = default) =>
        Parse(await ProviderHttp.SendJsonAsync(
            _provider.Http,
            ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"messages/batches/{batchId}"), _provider.Headers(null, [])),
            cancellationToken).ConfigureAwait(false));

    public async Task<BatchJob> CancelAsync(string batchId, CancellationToken cancellationToken = default) =>
        Parse(await ProviderHttp.SendJsonAsync(
            _provider.Http,
            ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url($"messages/batches/{batchId}/cancel"), _provider.Headers(null, [])),
            cancellationToken).ConfigureAwait(false));

    public async IAsyncEnumerable<BatchResultItem> GetResultsAsync(string batchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"messages/batches/{batchId}/results"), _provider.Headers(null, []));
        using var response = await ProviderHttp.SendAsync(_provider.Http, request, stream: true, cancellationToken).ConfigureAwait(false);
        await foreach (var line in ProviderHttp.ReadJsonLinesAsync(response, cancellationToken).ConfigureAwait(false))
        {
            var customId = (string?)line["custom_id"] ?? string.Empty;
            var result = line["result"] as JsonObject;
            yield return (string?)result?["type"] switch
            {
                "succeeded" when result!["message"] is JsonObject message => new BatchResultItem(customId, AnthropicMessagesLanguageModel.ParseResponse(message), null),
                "errored" => new BatchResultItem(customId, null, (string?)result!["error"]?["error"]?["message"] ?? (string?)result["error"]?["message"] ?? "errored"),
                var other => new BatchResultItem(customId, null, other ?? "unknown"),
            };
        }
    }

    internal static BatchJob Parse(JsonObject json)
    {
        var counts = json["request_counts"] as JsonObject ?? [];
        int Count(string key) => (int?)counts[key] ?? 0;
        var succeeded = Count("succeeded");
        var failed = Count("errored") + Count("expired") + Count("canceled");
        var status = (string?)json["processing_status"] switch
        {
            "in_progress" => BatchStatus.InProgress,
            "canceling" => BatchStatus.Canceling,
            "ended" => Count("canceled") > 0 && succeeded == 0 ? BatchStatus.Canceled : BatchStatus.Completed,
            _ => BatchStatus.InProgress,
        };
        return new BatchJob((string?)json["id"] ?? string.Empty, status, new BatchCounts(Count("processing") + succeeded + failed, succeeded, failed))
        {
            CreatedAt = ProviderHttp.ParseTimestamp(json["created_at"]),
            EndedAt = ProviderHttp.ParseTimestamp(json["ended_at"]),
        };
    }
}

/// <summary>
/// Files API de Anthropic (<c>/v1/files</c>): sube un archivo una vez y referéncialo en varios
/// mensajes con <see cref="FilePart.FromFileId"/> o <see cref="ImagePart.FromFileId"/>.
/// </summary>
public sealed class AnthropicFileClient : IFileClient
{
    private readonly AnthropicProvider _provider;

    internal AnthropicFileClient(AnthropicProvider provider) => _provider = provider;

    public async Task<UploadedFile> UploadAsync(byte[] data, string filename, string mediaType, string? purpose = null, CancellationToken cancellationToken = default)
    {
        var request = ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url("files"), _provider.Headers(null, []));
        request.Content = ProviderHttp.FileForm(data, filename, mediaType);
        return Parse(await ProviderHttp.SendJsonAsync(_provider.Http, request, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<UploadedFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var json = await ProviderHttp.SendJsonAsync(
            _provider.Http,
            ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url("files"), _provider.Headers(null, [])),
            cancellationToken).ConfigureAwait(false);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(Parse).ToList();
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken = default) =>
        await ProviderHttp.SendJsonAsync(
            _provider.Http,
            ProviderHttp.CreateRequest(HttpMethod.Delete, _provider.Url($"files/{fileId}"), _provider.Headers(null, [])),
            cancellationToken).ConfigureAwait(false);

    private static UploadedFile Parse(JsonObject f) => new(
        (string?)f["id"] ?? string.Empty,
        (string?)f["filename"] ?? string.Empty,
        (string?)f["mime_type"],
        (long?)f["size_bytes"],
        ProviderHttp.ParseTimestamp(f["created_at"]));
}
