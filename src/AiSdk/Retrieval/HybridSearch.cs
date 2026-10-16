namespace AiSdk;

/// <summary>
/// Almacén que además de la búsqueda semántica ofrece búsqueda híbrida (vectores + palabras clave).
/// La búsqueda híbrida mejora mucho con códigos, nombres propios, números o siglas.
/// </summary>
public interface IHybridVectorStore : IVectorStore
{
    Task<IReadOnlyList<VectorSearchResult>> HybridSearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default);
}
