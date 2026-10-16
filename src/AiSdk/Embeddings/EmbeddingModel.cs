namespace AiSdk;

/// <summary>Modelo de embeddings (equivalente a <c>EmbeddingModelV2</c>).</summary>
public interface IEmbeddingModel
{
    string Provider { get; }

    string ModelId { get; }

    /// <summary>Máximo de valores por llamada (null = sin límite).</summary>
    int? MaxEmbeddingsPerCall { get; }

    Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default);
}

public sealed record EmbeddingUsage(int Tokens);

public sealed record EmbeddingModelResult(IReadOnlyList<float[]> Embeddings, EmbeddingUsage? Usage);

public sealed record EmbedResult(string Value, float[] Embedding, EmbeddingUsage? Usage);

public sealed record EmbedManyResult(IReadOnlyList<string> Values, IReadOnlyList<float[]> Embeddings, EmbeddingUsage Usage);
