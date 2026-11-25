namespace AiSdk;

/// <summary>Reranking model: orders documents by relevance to a query.</summary>
public interface IRerankingModel
{
    string Provider { get; }

    string ModelId { get; }

    Task<RerankModelResult> RerankAsync(RerankModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record RerankModelRequest(string Query, IReadOnlyList<string> Documents)
{
    /// <summary>Gets or sets how many results to return (null = all).</summary>
    public int? TopN { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }
}

/// <summary>Position in the original list and relevance score.</summary>
public sealed record RerankScore(int Index, double Score);

public sealed record RerankModelResult(IReadOnlyList<RerankScore> Ranking);

/// <summary>Reranked document.</summary>
public sealed record RankedDocument<T>(T Document, int OriginalIndex, double Score);

public sealed record RerankResult<T>(IReadOnlyList<RankedDocument<T>> Ranking)
{
    /// <summary>Gets the documents in order of relevance.</summary>
    public IReadOnlyList<T> RerankedDocuments => Ranking.Select(r => r.Document).ToList();
}

/// <summary>
/// Reranking without a dedicated model: orders by cosine similarity between the embeddings of the query
/// and of the documents. Useful with providers that only offer embeddings.
/// </summary>
public sealed class EmbeddingRerankingModel(IEmbeddingModel embeddingModel) : IRerankingModel
{
    public string Provider => embeddingModel.Provider;

    public string ModelId => embeddingModel.ModelId;

    public async Task<RerankModelResult> RerankAsync(RerankModelRequest request, CancellationToken cancellationToken = default)
    {
        var embeddings = await Ai.EmbedManyAsync(embeddingModel, [request.Query, .. request.Documents], cancellationToken).ConfigureAwait(false);
        var query = embeddings.Embeddings[0];
        var ranking = embeddings.Embeddings
            .Skip(1)
            .Select((e, i) => new RerankScore(i, Ai.CosineSimilarity(query, e)))
            .OrderByDescending(r => r.Score)
            .Take(request.TopN ?? int.MaxValue)
            .ToList();
        return new RerankModelResult(ranking);
    }
}
