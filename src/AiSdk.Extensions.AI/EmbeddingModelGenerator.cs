using Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Exposes an AiSdk <see cref="IEmbeddingModel"/> as a Microsoft.Extensions.AI
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>. Usually created with
/// <see cref="MicrosoftExtensionsAIExtensions.AsIEmbeddingGenerator(IEmbeddingModel, Uri?)"/>.
/// </summary>
/// <remarks>
/// Inputs are split into batches of <see cref="IEmbeddingModel.MaxEmbeddingsPerCall"/>. AiSdk embedding models have a
/// fixed model and size, so <see cref="EmbeddingGenerationOptions.ModelId"/> and
/// <see cref="EmbeddingGenerationOptions.Dimensions"/> are ignored.
/// </remarks>
public sealed class EmbeddingModelGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingModel _model;
    private readonly EmbeddingGeneratorMetadata _metadata;

    /// <summary>Creates the adapter.</summary>
    /// <param name="model">The AiSdk embedding model to expose.</param>
    /// <param name="providerUri">Endpoint reported in <see cref="EmbeddingGeneratorMetadata.ProviderUri"/>, if known.</param>
    public EmbeddingModelGenerator(IEmbeddingModel model, Uri? providerUri = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _metadata = new EmbeddingGeneratorMetadata(model.Provider, providerUri, model.ModelId);
    }

    internal IEmbeddingModel Model => _model;

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = values as IReadOnlyList<string> ?? [.. values];
        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        var createdAt = DateTimeOffset.UtcNow;
        int? tokens = null;
        foreach (var batch in inputs.Chunk(Math.Max(1, _model.MaxEmbeddingsPerCall ?? int.MaxValue)))
        {
            var embedded = await _model.EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
            result.AddRange(embedded.Embeddings.Select(vector => new Embedding<float>(vector) { ModelId = _model.ModelId, CreatedAt = createdAt }));
            if (embedded.Usage is { } usage)
            {
                tokens = (tokens ?? 0) + usage.Tokens;
            }
        }

        if (tokens is { } total)
        {
            result.Usage = new UsageDetails { InputTokenCount = total, TotalTokenCount = total };
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <see cref="EmbeddingGeneratorMetadata"/> (AiSdk provider name, model id and endpoint), the wrapped
    /// <see cref="IEmbeddingModel"/> (or any type it implements) and the generator itself. Keyed services are not supported.
    /// </remarks>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : serviceType.IsInstanceOfType(_model) ? _model
            : null;
    }

    /// <summary>Does nothing: the adapter does not own the wrapped model.</summary>
    public void Dispose()
    {
    }
}
