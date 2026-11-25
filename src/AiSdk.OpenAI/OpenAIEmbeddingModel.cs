using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.OpenAI;

/// <summary>OpenAI embedding model (<c>/embeddings</c>).</summary>
public sealed class OpenAIEmbeddingModel : IEmbeddingModel
{
    private readonly OpenAIProvider _provider;
    private readonly int? _dimensions;

    internal OpenAIEmbeddingModel(string modelId, int? dimensions, OpenAIProvider provider)
    {
        ModelId = modelId;
        _dimensions = dimensions;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public int? MaxEmbeddingsPerCall => 2048;

    public async Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["input"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()),
        };
        if (_provider.Options.SendEmbeddingEncodingFormat)
        {
            body["encoding_format"] = "float";
        }
        if (_dimensions is { } d)
        {
            body["dimensions"] = d;
        }

        using var request = ProviderHttp.CreateJsonRequest(_provider.Url("embeddings"), body, _provider.Headers(null), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, request, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        var embeddings = (json["data"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .OrderBy(d => (int?)d["index"] ?? 0)
            .Select(d => (d["embedding"] as JsonArray ?? []).Select(x => (float)x!).ToArray())
            .ToList();

        return new EmbeddingModelResult(embeddings, new EmbeddingUsage((int?)json["usage"]?["prompt_tokens"] ?? 0));
    }
}
