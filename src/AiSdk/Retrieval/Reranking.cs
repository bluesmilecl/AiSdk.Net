namespace AiSdk;

/// <summary>Modelo de reranking: ordena documentos por relevancia respecto a una consulta.</summary>
public interface IRerankingModel
{
    string Provider { get; }

    string ModelId { get; }

    Task<RerankModelResult> RerankAsync(RerankModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record RerankModelRequest(string Query, IReadOnlyList<string> Documents)
{
    /// <summary>Cuántos resultados devolver (null = todos).</summary>
    public int? TopN { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }
}

/// <summary>Posición en la lista original y puntuación de relevancia.</summary>
public sealed record RerankScore(int Index, double Score);

public sealed record RerankModelResult(IReadOnlyList<RerankScore> Ranking);

/// <summary>Documento reordenado.</summary>
public sealed record RankedDocument<T>(T Document, int OriginalIndex, double Score);

public sealed record RerankResult<T>(IReadOnlyList<RankedDocument<T>> Ranking)
{
    /// <summary>Documentos en orden de relevancia.</summary>
    public IReadOnlyList<T> RerankedDocuments => Ranking.Select(r => r.Document).ToList();
}

/// <summary>
/// Reranking sin modelo dedicado: ordena por similitud coseno entre los embeddings de la consulta
/// y de los documentos. Útil con proveedores que solo ofrecen embeddings.
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
