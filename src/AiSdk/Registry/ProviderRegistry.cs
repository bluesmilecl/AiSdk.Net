namespace AiSdk;

/// <summary>Model provider (OpenAI, Anthropic, ...).</summary>
public interface IProvider
{
    string Name { get; }

    ILanguageModel LanguageModel(string modelId);

    /// <summary>Returns an embedding model or throws <see cref="NotSupportedException"/>.</summary>
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
/// Provider registry (equivalent to <c>createProviderRegistry</c>): resolves
/// models with <c>"provider:model"</c> identifiers, e.g. <c>"openai:gpt-5"</c>.
/// It also supports aliases (<c>"fast"</c> → <c>"anthropic:claude-haiku-5-5"</c>).
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ILanguageModel>> _aliases = new(StringComparer.OrdinalIgnoreCase);

    // Alias que se están resolviendo en este hilo (las fábricas son síncronas): detecta ciclos como a → b → a.
    [ThreadStatic]
    private static List<string>? _resolving;

    public char Separator { get; init; } = ':';

    public ProviderRegistry Register(IProvider provider) => Register(provider.Name, provider);

    public ProviderRegistry Register(string name, IProvider provider)
    {
        _providers[name] = provider;
        return this;
    }

    /// <summary>Registers an alias for a specific model (it can include middleware).</summary>
    public ProviderRegistry Alias(string alias, Func<ILanguageModel> factory)
    {
        _aliases[alias] = factory;
        return this;
    }

    public ProviderRegistry Alias(string alias, string target) => Alias(alias, () => LanguageModel(target));

    /// <summary>
    /// Registers an alias with failover: <c>Fallback("chat", "anthropic:claude-opus-5-5", "openai:gpt-5")</c>.
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
            var resolving = _resolving ??= [];
            if (resolving.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Ciclo de alias de modelos: {string.Join(" → ", resolving)} → {id}.");
            }

            resolving.Add(id);
            try
            {
                return factory();
            }
            finally
            {
                resolving.RemoveAt(resolving.Count - 1);
            }
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
