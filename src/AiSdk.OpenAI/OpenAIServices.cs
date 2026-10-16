using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.OpenAI;

/// <summary>Files API de OpenAI (<c>/files</c>).</summary>
public sealed class OpenAIFileClient : IFileClient
{
    private readonly OpenAIProvider _provider;

    internal OpenAIFileClient(OpenAIProvider provider) => _provider = provider;

    /// <summary>Sube un archivo. <c>purpose</c> por defecto es <c>user_data</c>; usa <c>assistants</c> para vector stores y <c>batch</c> para lotes.</summary>
    public async Task<UploadedFile> UploadAsync(byte[] data, string filename, string mediaType, string? purpose = null, CancellationToken cancellationToken = default)
    {
        var request = ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url("files"), _provider.Headers(null));
        request.Content = ProviderHttp.FileForm(data, filename, mediaType, [new("purpose", purpose ?? "user_data")]);
        return Parse(await ProviderHttp.SendJsonAsync(_provider.Http, request, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<UploadedFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var json = await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url("files"), _provider.Headers(null)), cancellationToken).ConfigureAwait(false);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(Parse).ToList();
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken = default) =>
        await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Delete, _provider.Url($"files/{fileId}"), _provider.Headers(null)), cancellationToken).ConfigureAwait(false);

    /// <summary>Descarga el contenido de un archivo.</summary>
    public async Task<byte[]> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
    {
        using var request = ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"files/{fileId}/content"), _provider.Headers(null));
        using var response = await ProviderHttp.SendAsync(_provider.Http, request, stream: false, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UploadedFile Parse(JsonObject f) => new(
        (string?)f["id"] ?? string.Empty,
        (string?)f["filename"] ?? string.Empty,
        null,
        (long?)f["bytes"],
        ProviderHttp.ParseTimestamp(f["created_at"]));
}

/// <summary>Estado de un vector store de OpenAI.</summary>
public sealed record OpenAIVectorStore(string Id, string Name, string Status, int FilesCompleted, int FilesInProgress, int FilesFailed)
{
    public bool IsReady => Status == "completed" && FilesInProgress == 0;
}

/// <summary>Resultado de búsqueda en un vector store.</summary>
public sealed record OpenAIVectorStoreHit(string FileId, string Filename, double Score, string Text);

/// <summary>
/// Vector stores de OpenAI (<c>/vector_stores</c>): indexa archivos para la herramienta
/// <see cref="OpenAITools.FileSearch"/> o búscalos directamente.
/// </summary>
public sealed class OpenAIVectorStoreClient
{
    private readonly OpenAIProvider _provider;

    internal OpenAIVectorStoreClient(OpenAIProvider provider) => _provider = provider;

    public async Task<OpenAIVectorStore> CreateAsync(string name, IEnumerable<string>? fileIds = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["name"] = name };
        if (fileIds is not null)
        {
            body["file_ids"] = new JsonArray(fileIds.Select(id => (JsonNode?)id).ToArray());
        }

        return Parse(await PostAsync("vector_stores", body, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OpenAIVectorStore> GetAsync(string vectorStoreId, CancellationToken cancellationToken = default) =>
        Parse(await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"vector_stores/{vectorStoreId}"), _provider.Headers(null)), cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<OpenAIVectorStore>> ListAsync(CancellationToken cancellationToken = default)
    {
        var json = await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url("vector_stores"), _provider.Headers(null)), cancellationToken).ConfigureAwait(false);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(Parse).ToList();
    }

    /// <summary>Añade un archivo (subido con <c>purpose: "assistants"</c> o <c>"user_data"</c>) al vector store.</summary>
    public Task AddFileAsync(string vectorStoreId, string fileId, CancellationToken cancellationToken = default) =>
        PostAsync($"vector_stores/{vectorStoreId}/files", new JsonObject { ["file_id"] = fileId }, cancellationToken);

    /// <summary>Sube un archivo y lo añade al vector store en un paso.</summary>
    public async Task<UploadedFile> UploadFileAsync(string vectorStoreId, byte[] data, string filename, string mediaType, CancellationToken cancellationToken = default)
    {
        var file = await _provider.Files.UploadAsync(data, filename, mediaType, "assistants", cancellationToken).ConfigureAwait(false);
        await AddFileAsync(vectorStoreId, file.Id, cancellationToken).ConfigureAwait(false);
        return file;
    }

    /// <summary>Espera a que todos los archivos estén indexados.</summary>
    public async Task<OpenAIVectorStore> WaitUntilReadyAsync(string vectorStoreId, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var store = await GetAsync(vectorStoreId, cancellationToken).ConfigureAwait(false);
            if (store.IsReady || store.Status == "expired")
            {
                return store;
            }

            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Búsqueda semántica directa en el vector store.</summary>
    public async Task<IReadOnlyList<OpenAIVectorStoreHit>> SearchAsync(string vectorStoreId, string query, int maxResults = 10, CancellationToken cancellationToken = default)
    {
        var json = await PostAsync($"vector_stores/{vectorStoreId}/search", new JsonObject { ["query"] = query, ["max_num_results"] = maxResults }, cancellationToken).ConfigureAwait(false);
        return (json["data"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(d => new OpenAIVectorStoreHit(
                (string?)d["file_id"] ?? string.Empty,
                (string?)d["filename"] ?? string.Empty,
                (double?)d["score"] ?? 0,
                string.Concat((d["content"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (string?)c["text"]))))
            .ToList();
    }

    public async Task DeleteAsync(string vectorStoreId, CancellationToken cancellationToken = default) =>
        await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Delete, _provider.Url($"vector_stores/{vectorStoreId}"), _provider.Headers(null)), cancellationToken).ConfigureAwait(false);

    private Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken cancellationToken) =>
        ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateJsonRequest(_provider.Url(path), body, _provider.Headers(null), stream: false), cancellationToken);

    private static OpenAIVectorStore Parse(JsonObject v) => new(
        (string?)v["id"] ?? string.Empty,
        (string?)v["name"] ?? string.Empty,
        (string?)v["status"] ?? "unknown",
        (int?)v["file_counts"]?["completed"] ?? 0,
        (int?)v["file_counts"]?["in_progress"] ?? 0,
        (int?)v["file_counts"]?["failed"] ?? 0);
}

/// <summary>
/// Batch API de OpenAI (<c>/batches</c>): sube las peticiones como JSONL y las procesa en
/// hasta 24 h con descuento. Usa la API Responses o Chat Completions.
/// </summary>
public sealed class OpenAIBatchClient : IBatchClient
{
    private readonly OpenAIProvider _provider;
    private readonly string _modelId;
    private readonly bool _useResponses;

    internal OpenAIBatchClient(OpenAIProvider provider, string modelId, bool useResponses)
    {
        _provider = provider;
        _modelId = modelId;
        _useResponses = useResponses;
    }

    private string Endpoint => _useResponses ? "/v1/responses" : "/v1/chat/completions";

    public async Task<BatchJob> CreateAsync(IReadOnlyList<BatchRequestItem> requests, CancellationToken cancellationToken = default)
    {
        var jsonl = new StringBuilder();
        foreach (var item in requests)
        {
            var body = _useResponses
                ? ((OpenAIResponsesLanguageModel)_provider.Responses(_modelId)).BuildBody(item.Request, stream: false).Body
                : ((OpenAIChatLanguageModel)_provider.Chat(_modelId)).BuildBody(item.Request, stream: false);
            jsonl.Append(new JsonObject
            {
                ["custom_id"] = item.CustomId,
                ["method"] = "POST",
                ["url"] = Endpoint,
                ["body"] = body,
            }.ToJsonString()).Append('\n');
        }

        var file = await _provider.Files.UploadAsync(Encoding.UTF8.GetBytes(jsonl.ToString()), "batch.jsonl", "application/jsonl", "batch", cancellationToken).ConfigureAwait(false);
        var job = await ProviderHttp.SendJsonAsync(
            _provider.Http,
            ProviderHttp.CreateJsonRequest(_provider.Url("batches"), new JsonObject
            {
                ["input_file_id"] = file.Id,
                ["endpoint"] = Endpoint,
                ["completion_window"] = "24h",
            }, _provider.Headers(null), stream: false),
            cancellationToken).ConfigureAwait(false);
        return Parse(job);
    }

    public async Task<BatchJob> GetAsync(string batchId, CancellationToken cancellationToken = default) =>
        Parse(await GetRawAsync(batchId, cancellationToken).ConfigureAwait(false));

    public async Task<BatchJob> CancelAsync(string batchId, CancellationToken cancellationToken = default) =>
        Parse(await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url($"batches/{batchId}/cancel"), _provider.Headers(null)), cancellationToken).ConfigureAwait(false));

    public async IAsyncEnumerable<BatchResultItem> GetResultsAsync(string batchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var job = await GetRawAsync(batchId, cancellationToken).ConfigureAwait(false);
        foreach (var fileId in new[] { (string?)job["output_file_id"], (string?)job["error_file_id"] }.OfType<string>())
        {
            using var request = ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"files/{fileId}/content"), _provider.Headers(null));
            using var response = await ProviderHttp.SendAsync(_provider.Http, request, stream: true, cancellationToken).ConfigureAwait(false);
            await foreach (var line in ProviderHttp.ReadJsonLinesAsync(response, cancellationToken).ConfigureAwait(false))
            {
                var customId = (string?)line["custom_id"] ?? string.Empty;
                var status = (int?)line["response"]?["status_code"];
                if (status is >= 200 and < 300 && line["response"]?["body"] is JsonObject body)
                {
                    yield return new BatchResultItem(customId, _useResponses ? OpenAIResponsesLanguageModel.ParseResponse(body) : OpenAIChatLanguageModel.ParseResponse(body), null);
                }
                else
                {
                    var error = (string?)line["error"]?["message"]
                                ?? (string?)line["response"]?["body"]?["error"]?["message"]
                                ?? $"HTTP {status}";
                    yield return new BatchResultItem(customId, null, error);
                }
            }
        }
    }

    private Task<JsonObject> GetRawAsync(string batchId, CancellationToken cancellationToken) =>
        ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"batches/{batchId}"), _provider.Headers(null)), cancellationToken);

    internal static BatchJob Parse(JsonObject json)
    {
        var counts = json["request_counts"] as JsonObject ?? [];
        var status = (string?)json["status"] switch
        {
            "validating" or "in_progress" or "finalizing" => BatchStatus.InProgress,
            "completed" => BatchStatus.Completed,
            "failed" => BatchStatus.Failed,
            "expired" => BatchStatus.Expired,
            "cancelling" => BatchStatus.Canceling,
            "cancelled" => BatchStatus.Canceled,
            _ => BatchStatus.InProgress,
        };
        return new BatchJob((string?)json["id"] ?? string.Empty, status, new BatchCounts((int?)counts["total"] ?? 0, (int?)counts["completed"] ?? 0, (int?)counts["failed"] ?? 0))
        {
            CreatedAt = ProviderHttp.ParseTimestamp(json["created_at"]),
            EndedAt = ProviderHttp.ParseTimestamp(json["completed_at"] ?? json["failed_at"] ?? json["expired_at"] ?? json["cancelled_at"]),
        };
    }
}
