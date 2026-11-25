namespace AiSdk;

/// <summary>
/// Store that, in addition to semantic search, offers hybrid search (vectors + keywords).
/// Hybrid search greatly improves results with codes, proper names, numbers or acronyms.
/// </summary>
public interface IHybridVectorStore : IVectorStore
{
    Task<IReadOnlyList<VectorSearchResult>> HybridSearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default);
}
