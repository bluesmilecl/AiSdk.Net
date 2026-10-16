namespace AiSdk;

/// <summary>Proveedor de modelos (OpenAI, Anthropic, ...).</summary>
public interface IProvider
{
    string Name { get; }

    ILanguageModel LanguageModel(string modelId);

    /// <summary>Devuelve un modelo de embeddings o lanza <see cref="NotSupportedException"/>.</summary>
    IEmbeddingModel EmbeddingModel(string modelId) =>
        throw new NotSupportedException($"El proveedor '{Name}' no ofrece modelos de embeddings.");

    IImageModel ImageModel(string modelId) =>
        throw new NotSupportedException($"El proveedor '{Name}' no ofrece modelos de imágenes.");

    ISpeechModel SpeechModel(string modelId) =>
        throw new NotSupportedException($"El proveedor '{Name}' no ofrece modelos de voz.");

    ITranscriptionModel TranscriptionModel(string modelId) =>
        throw new NotSupportedException($"El proveedor '{Name}' no ofrece modelos de transcripción.");

    IRerankingModel RerankingModel(string modelId) =>
        throw new NotSupportedException($"El proveedor '{Name}' no ofrece modelos de reranking.");
}

/// <summary>
/// Registro de proveedores (equivalente a <c>createProviderRegistry</c>): resuelve
/// modelos con identificadores <c>"proveedor:modelo"</c>, p. ej. <c>"openai:gpt-5"</c>.
/// También admite alias (<c>"rapido"</c> → <c>"anthropic:claude-haiku-5-5"</c>).
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ILanguageModel>> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public char Separator { get; init; } = ':';

    public ProviderRegistry Register(IProvider provider) => Register(provider.Name, provider);

    public ProviderRegistry Register(string name, IProvider provider)
    {
        _providers[name] = provider;
        return this;
    }

    /// <summary>Registra un alias para un modelo concreto (puede incluir middleware).</summary>
    public ProviderRegistry Alias(string alias, Func<ILanguageModel> factory)
    {
        _aliases[alias] = factory;
        return this;
    }

    public ProviderRegistry Alias(string alias, string target) => Alias(alias, () => LanguageModel(target));

    /// <summary>
    /// Registra un alias con conmutación por error: <c>Fallback("chat", "anthropic:claude-opus-5-5", "openai:gpt-5")</c>.
    /// </summary>
    public ProviderRegistry Fallback(string alias, params string[] modelIds)
    {
        if (modelIds.Length == 0)
        {
            throw new ArgumentException("Se necesita al menos un modelo.", nameof(modelIds));
        }

        return Alias(alias, () => new FallbackLanguageModel(modelIds.Select(LanguageModel).ToList()));
    }

    public IReadOnlyCollection<string> Providers => _providers.Keys;

    public ILanguageModel LanguageModel(string id)
    {
        if (_aliases.TryGetValue(id, out var factory))
        {
            return factory();
        }

        var (provider, modelId) = Split(id);
        return provider.LanguageModel(modelId);
    }

    public IEmbeddingModel EmbeddingModel(string id)
    {
        var (provider, modelId) = Split(id);
        return provider.EmbeddingModel(modelId);
    }

    public IImageModel ImageModel(string id)
    {
        var (provider, modelId) = Split(id);
        return provider.ImageModel(modelId);
    }

    public ISpeechModel SpeechModel(string id)
    {
        var (provider, modelId) = Split(id);
        return provider.SpeechModel(modelId);
    }

    public ITranscriptionModel TranscriptionModel(string id)
    {
        var (provider, modelId) = Split(id);
        return provider.TranscriptionModel(modelId);
    }

    public IRerankingModel RerankingModel(string id)
    {
        var (provider, modelId) = Split(id);
        return provider.RerankingModel(modelId);
    }

    private (IProvider Provider, string ModelId) Split(string id)
    {
        var index = id.IndexOf(Separator);
        if (index <= 0)
        {
            throw new ArgumentException($"Identificador inválido '{id}'. Usa el formato 'proveedor{Separator}modelo'.", nameof(id));
        }

        var name = id[..index];
        return _providers.TryGetValue(name, out var provider)
            ? (provider, id[(index + 1)..])
            : throw new KeyNotFoundException($"Proveedor '{name}' no registrado. Registrados: {string.Join(", ", _providers.Keys)}.");
    }
}
