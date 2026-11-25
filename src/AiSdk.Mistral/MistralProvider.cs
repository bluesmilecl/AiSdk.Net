using AiSdk.OpenAI;

namespace AiSdk.Mistral;

/// <summary>Mistral provider options.</summary>
public sealed class MistralProviderOptions
{
    /// <summary>API key. If null, <c>MISTRAL_API_KEY</c> is used.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.mistral.ai/v1";

    public string Name { get; set; } = "mistral";

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Mistral AI provider. The Mistral API follows the Chat Completions format with a few
/// differences (<c>max_tokens</c>, <c>random_seed</c>, <c>tool_choice: "any"</c>, reasoning in
/// <c>thinking</c> blocks), which this provider configures on top of the OpenAI client.
/// </summary>
public sealed class MistralProvider : IProvider
{
    private readonly OpenAIProvider _inner;
    private readonly MistralProviderOptions _options;

    public MistralProvider(MistralProviderOptions? options = null)
    {
        options ??= new MistralProviderOptions();
        _options = options;
        _inner = new OpenAIProvider(new OpenAIProviderOptions
        {
            ApiKey = options.ApiKey,
            ApiKeyEnvironmentVariable = "MISTRAL_API_KEY",
            BaseUrl = options.BaseUrl,
            Name = options.Name,
            Headers = options.Headers,
            HttpClient = options.HttpClient,
            UseLegacyMaxTokens = true,
            RequiredToolChoice = "any",
            SeedParameterName = "random_seed",
            SendStreamOptions = false,
            SendEmbeddingEncodingFormat = false,
        });
    }

    public MistralProvider(Action<MistralProviderOptions> configure)
        : this(Configure(configure))
    {
    }

    /// <summary>Default instance (API key from <c>MISTRAL_API_KEY</c>).</summary>
    public static MistralProvider Default { get; } = new();

    public string Name => _inner.Name;

    /// <summary>Chat model, e.g. <c>"mistral-large-latest"</c>, <c>"magistral-medium-latest"</c>.</summary>
    public ILanguageModel Chat(string modelId) => _inner.Chat(modelId);

    public ILanguageModel LanguageModel(string modelId) => Chat(modelId);

    public ILanguageModel this[string modelId] => Chat(modelId);

    /// <summary>Embedding model, e.g. <c>"mistral-embed"</c>.</summary>
    public IEmbeddingModel Embedding(string modelId = "mistral-embed") => _inner.Embedding(modelId);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    /// <summary>Mistral OCR engine (<c>mistral-ocr-latest</c>): images and full PDFs to Markdown.</summary>
    public IOcrEngine Ocr(string modelId = "mistral-ocr-latest") => new MistralOcr(_options, modelId);

    private static MistralProviderOptions Configure(Action<MistralProviderOptions> configure)
    {
        var options = new MistralProviderOptions();
        configure(options);
        return options;
    }
}
