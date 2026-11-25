using AiSdk.Http;

namespace AiSdk.OpenAI;

/// <summary>Options for the OpenAI / OpenAI-compatible provider.</summary>
public sealed class OpenAIProviderOptions
{
    /// <summary>API key. If null, the <see cref="ApiKeyEnvironmentVariable"/> environment variable is used.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Environment variable holding the API key when <see cref="ApiKey"/> is null. If not set,
    /// <c>OPENAI_API_KEY</c> is used only when <see cref="BaseUrl"/> is the official OpenAI endpoint
    /// (<c>https://api.openai.com</c>): the OpenAI key is never sent to other servers (Ollama, Groq,
    /// OpenRouter...). For a compatible server, specify its own variable, e.g. <c>"GROQ_API_KEY"</c>.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>Base URL. Defaults to <c>https://api.openai.com/v1</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string? Organization { get; set; }

    public string? Project { get; set; }

    /// <summary>Provider name (for the registry and telemetry).</summary>
    public string Name { get; set; } = "openai";

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Sends <c>max_tokens</c> instead of <c>max_completion_tokens</c>
    /// (required by some older compatible servers).
    /// </summary>
    public bool UseLegacyMaxTokens { get; set; }

    /// <summary>Allows not requiring an API key (e.g. local Ollama).</summary>
    public bool RequireApiKey { get; set; } = true;

    /// <summary>True if <see cref="BaseUrl"/> is the official OpenAI endpoint (<c>https://api.openai.com</c>).</summary>
    internal bool IsOfficialEndpoint =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.Equals(uri.IdnHost, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>tool_choice</c> value for <see cref="ToolChoiceKind.Required"/> (Mistral uses <c>"any"</c>).</summary>
    public string RequiredToolChoice { get; set; } = "required";

    /// <summary>Name of the seed parameter (Mistral uses <c>"random_seed"</c>).</summary>
    public string SeedParameterName { get; set; } = "seed";

    /// <summary>Sends <c>stream_options.include_usage</c> when streaming (some servers do not accept it).</summary>
    public bool SendStreamOptions { get; set; } = true;

    /// <summary>Sends <c>encoding_format: "float"</c> for embeddings.</summary>
    public bool SendEmbeddingEncodingFormat { get; set; } = true;
}

/// <summary>
/// OpenAI provider: Chat Completions, Responses, embeddings, images, speech and transcription.
/// <see cref="Chat"/> works with any compatible server: Ollama, Groq, DeepSeek,
/// OpenRouter, LM Studio, vLLM...
/// </summary>
public sealed class OpenAIProvider : IProvider
{
    private readonly OpenAIProviderOptions _options;

    public OpenAIProvider(OpenAIProviderOptions? options = null)
    {
        _options = options ?? new OpenAIProviderOptions();
    }

    public OpenAIProvider(Action<OpenAIProviderOptions> configure)
    {
        _options = new OpenAIProviderOptions();
        configure(_options);
    }

    /// <summary>Default instance (API key from <c>OPENAI_API_KEY</c>).</summary>
    public static OpenAIProvider Default { get; } = new();

    /// <summary>
    /// Provider for an OpenAI-compatible server (Ollama, Groq, OpenRouter, vLLM...). It never reads
    /// <c>OPENAI_API_KEY</c>: pass the key in <paramref name="apiKey"/> or the name of its environment variable in
    /// <paramref name="apiKeyEnvironmentVariable"/> (with neither, requests are sent without <c>Authorization</c>).
    /// </summary>
    public static OpenAIProvider Compatible(string name, string baseUrl, string? apiKey = null, string? apiKeyEnvironmentVariable = null) => new(new OpenAIProviderOptions
    {
        Name = name,
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
        RequireApiKey = apiKey is not null || apiKeyEnvironmentVariable is not null,
    });

    public string Name => _options.Name;

    /// <summary>Model over Chat Completions (<c>/chat/completions</c>), compatible with OpenAI-style servers.</summary>
    public ILanguageModel Chat(string modelId) => new OpenAIChatLanguageModel(modelId, this);

    /// <summary>
    /// Model over the Responses API (<c>/responses</c>), recommended by OpenAI: supports
    /// server tools (<see cref="OpenAITools"/>) and reasoning summaries.
    /// </summary>
    public ILanguageModel Responses(string modelId) => new OpenAIResponsesLanguageModel(modelId, this);

    /// <summary>
    /// Default model for the registry (<c>"openai:gpt-5"</c>): the Responses API on OpenAI and
    /// Chat Completions on compatible servers (when <c>BaseUrl</c> is changed).
    /// </summary>
    public ILanguageModel LanguageModel(string modelId) =>
        _options.IsOfficialEndpoint ? Responses(modelId) : Chat(modelId);

    /// <summary>Shortcut: <c>openai["gpt-5"]</c> (equivalent to <see cref="LanguageModel"/>).</summary>
    public ILanguageModel this[string modelId] => LanguageModel(modelId);

    /// <summary>Image model, e.g. <c>"gpt-image-1"</c>.</summary>
    public IImageModel Image(string modelId) => new OpenAIImageModel(modelId, this);

    public IImageModel ImageModel(string modelId) => Image(modelId);

    /// <summary>Speech model, e.g. <c>"gpt-4o-mini-tts"</c>.</summary>
    public ISpeechModel Speech(string modelId) => new OpenAISpeechModel(modelId, this);

    public ISpeechModel SpeechModel(string modelId) => Speech(modelId);

    /// <summary>Files API (<c>/files</c>).</summary>
    public OpenAIFileClient Files => new(this);

    /// <summary>Vector stores for the <see cref="OpenAITools.FileSearch"/> tool.</summary>
    public OpenAIVectorStoreClient VectorStores => new(this);

    /// <summary>Batch API for the specified model (over the Responses API by default).</summary>
    public OpenAIBatchClient Batches(string modelId, bool useResponsesApi = true) => new(this, modelId, useResponsesApi);

    /// <summary>Transcription model, e.g. <c>"gpt-4o-transcribe"</c> or <c>"whisper-1"</c>.</summary>
    public ITranscriptionModel Transcription(string modelId) => new OpenAITranscriptionModel(modelId, this);

    public ITranscriptionModel TranscriptionModel(string modelId) => Transcription(modelId);

    /// <summary>Embedding model, e.g. <c>"text-embedding-3-small"</c>.</summary>
    public IEmbeddingModel Embedding(string modelId, int? dimensions = null) => new OpenAIEmbeddingModel(modelId, dimensions, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    internal OpenAIProviderOptions Options => _options;

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal Uri Url(string path) => new($"{_options.BaseUrl.TrimEnd('/')}/{path}");

    internal IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string>? extra)
    {
        // OPENAI_API_KEY solo se usa implícitamente con el endpoint oficial: con otra BaseUrl iría a un tercero.
        var variable = _options.ApiKeyEnvironmentVariable ?? (_options.IsOfficialEndpoint ? "OPENAI_API_KEY" : null);
        var key = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : variable is null ? null : Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(key) && _options.RequireApiKey)
        {
            throw new AiSdkException(variable is null
                ? $"Falta la API key de '{_options.Name}': pásala en ApiKey o indica ApiKeyEnvironmentVariable (OPENAI_API_KEY solo se lee con https://api.openai.com)."
                : $"Falta la API key de '{_options.Name}'. Pásala en las opciones o define la variable de entorno {variable}.");
        }

        if (!string.IsNullOrEmpty(key))
        {
            yield return new("Authorization", $"Bearer {key}");
        }

        if (_options.Organization is not null)
        {
            yield return new("OpenAI-Organization", _options.Organization);
        }

        if (_options.Project is not null)
        {
            yield return new("OpenAI-Project", _options.Project);
        }

        foreach (var h in _options.Headers)
        {
            yield return h;
        }

        foreach (var h in extra ?? new Dictionary<string, string>())
        {
            yield return h;
        }
    }
}
