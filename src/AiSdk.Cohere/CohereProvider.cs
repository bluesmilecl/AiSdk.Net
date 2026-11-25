using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.Cohere;

/// <summary>Cohere provider options.</summary>
public sealed class CohereProviderOptions
{
    /// <summary>API key. If null, <c>COHERE_API_KEY</c> is used.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.cohere.com/v2";

    public string Name { get; set; } = "cohere";

    public HttpClient? HttpClient { get; set; }
}

/// <summary>Cohere provider: reranking and embeddings.</summary>
public sealed class CohereProvider : IProvider
{
    private readonly CohereProviderOptions _options;

    public CohereProvider(CohereProviderOptions? options = null) => _options = options ?? new CohereProviderOptions();

    public static CohereProvider Default { get; } = new();

    public string Name => _options.Name;

    public ILanguageModel LanguageModel(string modelId) =>
        throw new NotSupportedException("El proveedor Cohere de AiSdk solo ofrece reranking y embeddings.");

    /// <summary>Reranking model, e.g. <c>"rerank-v3.5"</c>.</summary>
    public IRerankingModel Reranking(string modelId = "rerank-v3.5") => new CohereRerankingModel(modelId, this);

    public IRerankingModel RerankingModel(string modelId) => Reranking(modelId);

    /// <summary>Embeddings, e.g. <c>"embed-v4.0"</c>.</summary>
    /// <param name="modelId">Model id.</param>
    /// <param name="inputType"><c>search_document</c>, <c>search_query</c>, <c>classification</c> or <c>clustering</c>.</param>
    /// <param name="dimensions">Output dimensions (models that support it).</param>
    public IEmbeddingModel Embedding(string modelId = "embed-v4.0", string inputType = "search_document", int? dimensions = null) =>
        new CohereEmbeddingModel(modelId, inputType, dimensions, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    internal async Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        var key = ProviderHttp.LoadApiKey(_options.ApiKey, "COHERE_API_KEY", "Cohere");
        var request = ProviderHttp.CreateJsonRequest(new Uri($"{_options.BaseUrl.TrimEnd('/')}/{path}"), body, [new("Authorization", $"Bearer {key}")], stream: false);
        return await ProviderHttp.SendJsonAsync(_options.HttpClient ?? ProviderHttp.DefaultClient, request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reranking with Cohere Rerank (<c>/v2/rerank</c>).</summary>
public sealed class CohereRerankingModel : IRerankingModel
{
    private readonly CohereProvider _provider;

    internal CohereRerankingModel(string modelId, CohereProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<RerankModelResult> RerankAsync(RerankModelRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["query"] = request.Query,
            ["documents"] = new JsonArray(request.Documents.Select(d => (JsonNode?)d).ToArray()),
        };
        if (request.TopN is { } topN)
        {
            body["top_n"] = topN;
        }

        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("cohere"));
        var json = await _provider.PostAsync("rerank", body, cancellationToken).ConfigureAwait(false);
        return new RerankModelResult((json["results"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(r => new RerankScore((int?)r["index"] ?? -1, (double?)r["relevance_score"] ?? 0))
            .ToList());
    }
}

/// <summary>Embeddings with Cohere Embed (<c>/v2/embed</c>).</summary>
public sealed class CohereEmbeddingModel : IEmbeddingModel
{
    private readonly CohereProvider _provider;
    private readonly string _inputType;
    private readonly int? _dimensions;

    internal CohereEmbeddingModel(string modelId, string inputType, int? dimensions, CohereProvider provider)
    {
        ModelId = modelId;
        _inputType = inputType;
        _dimensions = dimensions;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public int? MaxEmbeddingsPerCall => 96;

    public async Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["texts"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()),
            ["input_type"] = _inputType,
            ["embedding_types"] = new JsonArray("float"),
        };
        if (_dimensions is { } d)
        {
            body["output_dimension"] = d;
        }

        var json = await _provider.PostAsync("embed", body, cancellationToken).ConfigureAwait(false);
        var vectors = (json["embeddings"]?["float"] as JsonArray ?? [])
            .Select(e => (e as JsonArray ?? []).Select(x => (float)x!).ToArray())
            .ToList();
        var tokens = (int?)json["meta"]?["billed_units"]?["input_tokens"];
        return new EmbeddingModelResult(vectors, tokens is null ? null : new EmbeddingUsage(tokens.Value));
    }
}
