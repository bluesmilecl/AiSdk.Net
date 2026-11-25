using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.Google;

/// <summary>Gemini embeddings (<c>batchEmbedContents</c>).</summary>
public sealed class GoogleEmbeddingModel : IEmbeddingModel
{
    private readonly GoogleProvider _provider;
    private readonly int? _dimensions;
    private readonly string? _taskType;

    internal GoogleEmbeddingModel(string modelId, int? dimensions, string? taskType, GoogleProvider provider)
    {
        ModelId = modelId;
        _dimensions = dimensions;
        _taskType = taskType;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public int? MaxEmbeddingsPerCall => 100;

    public async Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var requests = new JsonArray();
        foreach (var value in values)
        {
            var item = new JsonObject
            {
                ["model"] = GoogleProvider.ModelName(ModelId),
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = value }) },
            };
            if (_taskType is not null)
            {
                item["taskType"] = _taskType;
            }

            if (_dimensions is { } d)
            {
                item["outputDimensionality"] = d;
            }

            requests.Add(item);
        }

        using var http = ProviderHttp.CreateJsonRequest(_provider.ModelUrl(ModelId, "batchEmbedContents"), new JsonObject { ["requests"] = requests }, _provider.Headers(null), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var embeddings = (json["embeddings"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(e => (e["values"] as JsonArray ?? []).Select(v => (float)v!).ToArray())
            .ToList();
        return new EmbeddingModelResult(embeddings, null);
    }
}

/// <summary>Image generation with Imagen (<c>predict</c>).</summary>
public sealed class GoogleImageModel : IImageModel
{
    private readonly GoogleProvider _provider;

    internal GoogleImageModel(string modelId, GoogleProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public int? MaxImagesPerCall => 4;

    public async Task<ImageModelResult> GenerateAsync(ImageModelRequest request, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var parameters = new JsonObject { ["sampleCount"] = request.N };
        if (request.AspectRatio is not null)
        {
            parameters["aspectRatio"] = request.AspectRatio;
        }

        if (request.Size is not null)
        {
            warnings.Add("Imagen no admite Size; usa AspectRatio (p. ej. \"16:9\").");
        }

        if (request.Seed is { } seed)
        {
            parameters["seed"] = seed;
        }

        var body = new JsonObject
        {
            ["instances"] = new JsonArray(new JsonObject { ["prompt"] = request.Prompt }),
            ["parameters"] = parameters,
        };
        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("google"));

        using var http = ProviderHttp.CreateJsonRequest(_provider.ModelUrl(ModelId, "predict"), body, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        var images = (json["predictions"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(p => (string?)p["bytesBase64Encoded"] is not null)
            .Select(p => new GeneratedFile(Convert.FromBase64String((string)p["bytesBase64Encoded"]!), (string?)p["mimeType"] ?? "image/png"))
            .ToList();
        return new ImageModelResult(images) { Warnings = warnings };
    }
}
