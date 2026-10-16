using AiSdk.Anthropic;
using AiSdk.Bedrock;
using AiSdk.Cohere;
using AiSdk.Google;
using AiSdk.Mistral;
using AiSdk.OpenAI;
using AiSdk.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiSdk.Extensions.DependencyInjection;

/// <summary>Constructor fluido para configurar el SDK en el contenedor de dependencias.</summary>
public sealed class AiSdkBuilder
{
    private readonly List<Action<IServiceProvider, ProviderRegistry>> _registrations = [];

    internal AiSdkBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Registra un proveedor arbitrario.</summary>
    public AiSdkBuilder AddProvider(string name, Func<IServiceProvider, IProvider> factory)
    {
        _registrations.Add((sp, registry) => registry.Register(name, factory(sp)));
        return this;
    }

    /// <summary>Registra OpenAI (o un servidor compatible si se cambia <c>BaseUrl</c>/<c>Name</c>).</summary>
    public AiSdkBuilder AddOpenAI(Action<OpenAIProviderOptions>? configure = null)
    {
        var options = new OpenAIProviderOptions();
        configure?.Invoke(options);
        var clientName = $"AiSdk.{options.Name}";
        Services.AddHttpClient(clientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        return AddProvider(options.Name, sp =>
        {
            options.HttpClient ??= sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            return new OpenAIProvider(options);
        });
    }

    /// <summary>Registra Anthropic (Claude).</summary>
    public AiSdkBuilder AddAnthropic(Action<AnthropicProviderOptions>? configure = null)
    {
        var options = new AnthropicProviderOptions();
        configure?.Invoke(options);
        var clientName = $"AiSdk.{options.Name}";
        Services.AddHttpClient(clientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        return AddProvider(options.Name, sp =>
        {
            options.HttpClient ??= sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            return new AnthropicProvider(options);
        });
    }

    /// <summary>Registra Google Gemini.</summary>
    public AiSdkBuilder AddGoogle(Action<GoogleProviderOptions>? configure = null) =>
        AddHttpProvider<GoogleProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new GoogleProvider(o));

    /// <summary>Registra Mistral AI.</summary>
    public AiSdkBuilder AddMistral(Action<MistralProviderOptions>? configure = null) =>
        AddHttpProvider<MistralProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new MistralProvider(o));

    /// <summary>Registra Amazon Bedrock.</summary>
    public AiSdkBuilder AddBedrock(Action<BedrockProviderOptions>? configure = null) =>
        AddHttpProvider<BedrockProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new BedrockProvider(o));

    /// <summary>Registra Cohere (reranking y embeddings).</summary>
    public AiSdkBuilder AddCohere(Action<CohereProviderOptions>? configure = null) =>
        AddHttpProvider<CohereProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new CohereProvider(o));

    private AiSdkBuilder AddHttpProvider<TOptions>(
        Action<TOptions>? configure,
        Func<TOptions, string> name,
        Action<TOptions, HttpClient> setClient,
        Func<TOptions, IProvider> create)
        where TOptions : new()
    {
        var options = new TOptions();
        configure?.Invoke(options);
        var providerName = name(options);
        var clientName = $"AiSdk.{providerName}";
        Services.AddHttpClient(clientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        return AddProvider(providerName, sp =>
        {
            setClient(options, sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName));
            return create(options);
        });
    }

    /// <summary>Registra el almacén de streams reanudables (en memoria por defecto).</summary>
    public AiSdkBuilder AddResumableStreams(IResumableStreamStore? store = null)
    {
        Services.TryAddSingleton(store ?? new InMemoryResumableStreamStore());
        return this;
    }

    /// <summary>Registra un modelo de reranking por defecto.</summary>
    public AiSdkBuilder AddDefaultRerankingModel(string modelId)
    {
        Services.TryAddSingleton<IRerankingModel>(sp => sp.GetRequiredService<ProviderRegistry>().RerankingModel(modelId));
        return this;
    }

    /// <summary>Registra un alias de modelo (p. ej. <c>"rapido"</c> → <c>"anthropic:claude-haiku-5-5"</c>).</summary>
    public AiSdkBuilder AddAlias(string alias, string target)
    {
        _registrations.Add((_, registry) => registry.Alias(alias, target));
        return this;
    }

    /// <summary>Registra un alias con conmutación por error entre varios modelos.</summary>
    public AiSdkBuilder AddFallback(string alias, params string[] modelIds)
    {
        _registrations.Add((_, registry) => registry.Fallback(alias, modelIds));
        return this;
    }

    /// <summary>Registra <see cref="ILanguageModel"/> como el modelo indicado (para inyectarlo directamente).</summary>
    public AiSdkBuilder AddDefaultModel(string modelId, params ILanguageModelMiddleware[] middleware)
    {
        Services.TryAddSingleton<ILanguageModel>(sp =>
        {
            var model = sp.GetRequiredService<ProviderRegistry>().LanguageModel(modelId);
            return middleware.Length > 0 ? model.WithMiddleware(middleware) : model;
        });
        return this;
    }

    /// <summary>Registra <see cref="IEmbeddingModel"/> por defecto.</summary>
    public AiSdkBuilder AddDefaultEmbeddingModel(string modelId)
    {
        Services.TryAddSingleton<IEmbeddingModel>(sp => sp.GetRequiredService<ProviderRegistry>().EmbeddingModel(modelId));
        return this;
    }

    /// <summary>Registra un agente basado en clase (hereda de <see cref="Agent"/>).</summary>
    public AiSdkBuilder AddAgent<TAgent>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TAgent : Agent
    {
        Services.Add(new ServiceDescriptor(typeof(TAgent), typeof(TAgent), lifetime));
        return this;
    }

    /// <summary>Registra un <see cref="ToolLoopAgent"/> con clave (keyed service).</summary>
    public AiSdkBuilder AddAgent(string key, Func<IServiceProvider, AgentSettings> settings)
    {
        Services.AddKeyedSingleton<IAgent>(key, (sp, _) => new ToolLoopAgent(settings(sp)));
        return this;
    }

    /// <summary>Usa un almacén de conversaciones propio (por defecto, en memoria).</summary>
    public AiSdkBuilder AddConversationStore<TStore>()
        where TStore : class, IConversationStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IConversationStore, TStore>());
        return this;
    }

    internal ProviderRegistry Build(IServiceProvider sp)
    {
        var registry = new ProviderRegistry();
        foreach (var registration in _registrations)
        {
            registration(sp, registry);
        }

        return registry;
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Añade el SDK: registra <see cref="ProviderRegistry"/> (singleton), los proveedores
    /// configurados y un <see cref="IConversationStore"/> en memoria.
    /// </summary>
    public static IServiceCollection AddAiSdk(this IServiceCollection services, Action<AiSdkBuilder> configure)
    {
        var builder = new AiSdkBuilder(services);
        configure(builder);
        services.TryAddSingleton(sp => builder.Build(sp));
        services.TryAddSingleton<IConversationStore, InMemoryConversationStore>();
        return services;
    }
}
