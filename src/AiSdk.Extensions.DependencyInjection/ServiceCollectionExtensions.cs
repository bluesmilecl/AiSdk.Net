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

/// <summary>Fluent builder for configuring the SDK in the dependency injection container.</summary>
public sealed class AiSdkBuilder
{
    private readonly List<Action<IServiceProvider, ProviderRegistry>> _registrations = [];

    internal AiSdkBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Registers an arbitrary provider.</summary>
    public AiSdkBuilder AddProvider(string name, Func<IServiceProvider, IProvider> factory)
    {
        _registrations.Add((sp, registry) => registry.Register(name, factory(sp)));
        return this;
    }

    /// <summary>Registers OpenAI (or a compatible server if <c>BaseUrl</c>/<c>Name</c> is changed).</summary>
    public AiSdkBuilder AddOpenAI(Action<OpenAIProviderOptions>? configure = null)
    {
        var options = new OpenAIProviderOptions();
        configure?.Invoke(options);
        var clientName = $"AiSdk.{options.Name}";
        AddProviderHttpClient(clientName);
        return AddProvider(options.Name, sp =>
        {
            options.HttpClient ??= sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            return new OpenAIProvider(options);
        });
    }

    /// <summary>Registers Anthropic (Claude).</summary>
    public AiSdkBuilder AddAnthropic(Action<AnthropicProviderOptions>? configure = null)
    {
        var options = new AnthropicProviderOptions();
        configure?.Invoke(options);
        var clientName = $"AiSdk.{options.Name}";
        AddProviderHttpClient(clientName);
        return AddProvider(options.Name, sp =>
        {
            options.HttpClient ??= sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            return new AnthropicProvider(options);
        });
    }

    /// <summary>Registers Google Gemini.</summary>
    public AiSdkBuilder AddGoogle(Action<GoogleProviderOptions>? configure = null) =>
        AddHttpProvider<GoogleProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new GoogleProvider(o));

    /// <summary>Registers Mistral AI.</summary>
    public AiSdkBuilder AddMistral(Action<MistralProviderOptions>? configure = null) =>
        AddHttpProvider<MistralProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new MistralProvider(o));

    /// <summary>Registers Amazon Bedrock.</summary>
    public AiSdkBuilder AddBedrock(Action<BedrockProviderOptions>? configure = null) =>
        AddHttpProvider<BedrockProviderOptions>(configure, o => o.Name, (o, c) => o.HttpClient ??= c, o => new BedrockProvider(o));

    /// <summary>Registers Cohere (reranking and embeddings).</summary>
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
        AddProviderHttpClient(clientName);
        return AddProvider(providerName, sp =>
        {
            setClient(options, sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName));
            return create(options);
        });
    }

    /// <summary>
    /// Named HTTP client for a provider: without automatic redirects (which would forward its own authentication
    /// headers, such as <c>x-api-key</c>, to another origin) and with <see cref="AiSdk.Http.SafeRedirectHandler"/>.
    /// </summary>
    private void AddProviderHttpClient(string clientName) =>
        Services.AddHttpClient(clientName, c => c.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(() => new AiSdk.Http.SafeRedirectHandler());

    /// <summary>Registers the resumable stream store (in memory by default).</summary>
    public AiSdkBuilder AddResumableStreams(IResumableStreamStore? store = null)
    {
        Services.TryAddSingleton(store ?? new InMemoryResumableStreamStore());
        return this;
    }

    /// <summary>
    /// Registers a singleton <see cref="AiSdk.Skills.SkillLibrary"/> with the skills from the specified folders
    /// (trusted, since they belong to the project itself). Use it in agents with <c>Skills = sp.GetRequiredService&lt;SkillLibrary&gt;()</c>.
    /// Skills with the <see cref="AiSdk.Skills.SkillLibrary.ProvenanceFileName"/> marker (e.g. created by an
    /// agent with <c>create_skill</c> in that same folder) are loaded as untrusted.
    /// </summary>
    public AiSdkBuilder AddSkills(params string[] directories) => AddSkills(library =>
    {
        foreach (var directory in directories)
        {
            library.LoadDirectory(directory, new AiSdk.Skills.SkillLoadOptions { Trusted = true });
        }
    });

    /// <summary>Registers a singleton <see cref="AiSdk.Skills.SkillLibrary"/> configured in code.</summary>
    public AiSdkBuilder AddSkills(Action<AiSdk.Skills.SkillLibrary> configure)
    {
        Services.TryAddSingleton(_ =>
        {
            var library = new AiSdk.Skills.SkillLibrary();
            configure(library);
            return library;
        });
        return this;
    }

    /// <summary>Registers a default reranking model.</summary>
    public AiSdkBuilder AddDefaultRerankingModel(string modelId)
    {
        Services.TryAddSingleton<IRerankingModel>(sp => sp.GetRequiredService<ProviderRegistry>().RerankingModel(modelId));
        return this;
    }

    /// <summary>Registers a model alias (e.g. <c>"fast"</c> → <c>"anthropic:claude-haiku-5-5"</c>).</summary>
    public AiSdkBuilder AddAlias(string alias, string target)
    {
        _registrations.Add((_, registry) => registry.Alias(alias, target));
        return this;
    }

    /// <summary>Registers an alias with failover across multiple models.</summary>
    public AiSdkBuilder AddFallback(string alias, params string[] modelIds)
    {
        _registrations.Add((_, registry) => registry.Fallback(alias, modelIds));
        return this;
    }

    /// <summary>Registers <see cref="ILanguageModel"/> as the specified model (to inject it directly).</summary>
    public AiSdkBuilder AddDefaultModel(string modelId, params ILanguageModelMiddleware[] middleware)
    {
        Services.TryAddSingleton<ILanguageModel>(sp =>
        {
            var model = sp.GetRequiredService<ProviderRegistry>().LanguageModel(modelId);
            return middleware.Length > 0 ? model.WithMiddleware(middleware) : model;
        });
        return this;
    }

    /// <summary>Registers the default <see cref="IEmbeddingModel"/>.</summary>
    public AiSdkBuilder AddDefaultEmbeddingModel(string modelId)
    {
        Services.TryAddSingleton<IEmbeddingModel>(sp => sp.GetRequiredService<ProviderRegistry>().EmbeddingModel(modelId));
        return this;
    }

    /// <summary>Registers a class-based agent (derived from <see cref="Agent"/>).</summary>
    public AiSdkBuilder AddAgent<TAgent>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TAgent : Agent
    {
        Services.Add(new ServiceDescriptor(typeof(TAgent), typeof(TAgent), lifetime));
        return this;
    }

    /// <summary>Registers a keyed <see cref="ToolLoopAgent"/> (keyed service).</summary>
    public AiSdkBuilder AddAgent(string key, Func<IServiceProvider, AgentSettings> settings)
    {
        Services.AddKeyedSingleton<IAgent>(key, (sp, _) => new ToolLoopAgent(settings(sp)));
        return this;
    }

    /// <summary>Uses a custom conversation store (in memory by default).</summary>
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
    /// Adds the SDK: registers <see cref="ProviderRegistry"/> (singleton), the configured
    /// providers and an in-memory <see cref="IConversationStore"/>.
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
