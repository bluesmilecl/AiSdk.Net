using Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Adapts a Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> to <see cref="IEmbeddingModel"/>.
/// Usually created with
/// <see cref="MicrosoftExtensionsAIExtensions.AsEmbeddingModel(IEmbeddingGenerator{string, Embedding{float}}, string?, string?)"/>.
/// </summary>
/// <param name="generator">The Microsoft.Extensions.AI embedding generator to adapt.</param>
/// <param name="provider">Provider name reported by <see cref="Provider"/>.</param>
/// <param name="modelId">Model id reported by <see cref="ModelId"/>.</param>
public sealed class EmbeddingGeneratorModel(IEmbeddingGenerator<string, Embedding<float>> generator, string provider, string modelId) : IEmbeddingModel
{
    /// <inheritdoc />
    public string Provider { get; } = provider;

    /// <inheritdoc />
    public string ModelId { get; } = modelId;

    /// <inheritdoc />
    public int? MaxEmbeddingsPerCall => null;

    internal IEmbeddingGenerator<string, Embedding<float>> Generator => generator;

    /// <inheritdoc />
    public async Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var result = await generator.GenerateAsync(values, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new EmbeddingModelResult(
            [.. result.Select(e => e.Vector.ToArray())],
            result.Usage?.InputTokenCount is { } tokens ? new EmbeddingUsage((int)tokens) : null);
    }
}
