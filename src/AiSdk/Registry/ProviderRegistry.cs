namespace AiSdk;

/// <summary>Proveedor de modelos (OpenAI, Anthropic, ...).</summary>
public interface IProvider
{
    string Name { get; }

    ILanguageModel LanguageModel(string modelId);

    /// <summary>Devuelve un modelo de embeddings o lanza <see cref="NotSupportedException"/>.</summary>
    IEmbeddingModel EmbeddingModel(string modelId) =>
        throw new NotSupportedException($"El proveedor '{Name}' no ofrece modelos de embeddings.");
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
