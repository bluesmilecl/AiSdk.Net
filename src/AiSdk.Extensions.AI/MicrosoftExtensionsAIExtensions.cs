using Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Conversions between AiSdk models and Microsoft.Extensions.AI abstractions, in both directions. Converting back
/// returns the original object instead of wrapping it twice.
/// </summary>
public static class MicrosoftExtensionsAIExtensions
{
    /// <summary>
    /// Converts an <see cref="IChatClient"/> into an <see cref="ILanguageModel"/>, usable with <see cref="Ai"/>, agents and middleware.
    /// </summary>
    /// <param name="client">The Microsoft.Extensions.AI chat client.</param>
    /// <param name="provider">Provider name; defaults to <see cref="ChatClientMetadata.ProviderName"/> or <c>"meai"</c>.</param>
    /// <param name="modelId">Model id; defaults to <see cref="ChatClientMetadata.DefaultModelId"/> or <c>"default"</c>.</param>
    /// <returns>
    /// The wrapped model when <paramref name="client"/> is a <see cref="LanguageModelChatClient"/> and no different
    /// provider or model id is requested; otherwise a <see cref="ChatClientLanguageModel"/>.
    /// </returns>
    public static ILanguageModel AsLanguageModel(this IChatClient client, string? provider = null, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client is LanguageModelChatClient adapter
            && (provider is null || provider == adapter.Model.Provider)
            && (modelId is null || modelId == adapter.Model.ModelId))
        {
            return adapter.Model;
        }

        var metadata = client.GetService<ChatClientMetadata>();
        return new ChatClientLanguageModel(
            client,
            provider ?? metadata?.ProviderName ?? "meai",
            modelId ?? metadata?.DefaultModelId ?? "default");
    }

    /// <summary>
    /// Converts an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> into an <see cref="IEmbeddingModel"/>.
    /// </summary>
    /// <param name="generator">The Microsoft.Extensions.AI embedding generator.</param>
    /// <param name="provider">Provider name; defaults to <see cref="EmbeddingGeneratorMetadata.ProviderName"/> or <c>"meai"</c>.</param>
    /// <param name="modelId">Model id; defaults to <see cref="EmbeddingGeneratorMetadata.DefaultModelId"/> or <c>"default"</c>.</param>
    /// <returns>
    /// The wrapped model when <paramref name="generator"/> is an <see cref="EmbeddingModelGenerator"/> and no different
    /// provider or model id is requested; otherwise an <see cref="EmbeddingGeneratorModel"/>.
    /// </returns>
    public static IEmbeddingModel AsEmbeddingModel(this IEmbeddingGenerator<string, Embedding<float>> generator, string? provider = null, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (generator is EmbeddingModelGenerator adapter
            && (provider is null || provider == adapter.Model.Provider)
            && (modelId is null || modelId == adapter.Model.ModelId))
        {
            return adapter.Model;
        }

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        return new EmbeddingGeneratorModel(
            generator,
            provider ?? metadata?.ProviderName ?? "meai",
            modelId ?? metadata?.DefaultModelId ?? "default");
    }

    /// <summary>
    /// Exposes an AiSdk <see cref="ILanguageModel"/> as a Microsoft.Extensions.AI <see cref="IChatClient"/>, for
    /// Semantic Kernel, Microsoft Agent Framework, <c>FunctionInvokingChatClient</c> and any other code built on
    /// Microsoft.Extensions.AI. The client never executes tools: it returns <see cref="FunctionCallContent"/>.
    /// See <see cref="LanguageModelChatClient"/> for the option mapping.
    /// </summary>
    /// <param name="model">The AiSdk model.</param>
    /// <param name="providerUri">Endpoint reported in <see cref="ChatClientMetadata.ProviderUri"/>, if known.</param>
    /// <returns>
    /// The wrapped client when <paramref name="model"/> is a <see cref="ChatClientLanguageModel"/> and no
    /// <paramref name="providerUri"/> is given; otherwise a <see cref="LanguageModelChatClient"/>.
    /// </returns>
    public static IChatClient AsChatClient(this ILanguageModel model, Uri? providerUri = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model is ChatClientLanguageModel adapter && providerUri is null
            ? adapter.Client
            : new LanguageModelChatClient(model, providerUri);
    }

    /// <summary>
    /// Exposes an AiSdk <see cref="IEmbeddingModel"/> as a Microsoft.Extensions.AI
    /// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> of <see cref="Embedding{T}"/> of <see cref="float"/>.
    /// </summary>
    /// <param name="model">The AiSdk embedding model.</param>
    /// <param name="providerUri">Endpoint reported in <see cref="EmbeddingGeneratorMetadata.ProviderUri"/>, if known.</param>
    /// <returns>
    /// The wrapped generator when <paramref name="model"/> is an <see cref="EmbeddingGeneratorModel"/> and no
    /// <paramref name="providerUri"/> is given; otherwise an <see cref="EmbeddingModelGenerator"/>.
    /// </returns>
    public static IEmbeddingGenerator<string, Embedding<float>> AsEmbeddingGenerator(this IEmbeddingModel model, Uri? providerUri = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model is EmbeddingGeneratorModel adapter && providerUri is null
            ? adapter.Generator
            : new EmbeddingModelGenerator(model, providerUri);
    }
}
