using AiSdk.Http;

namespace AiSdk.OpenAI;

/// <summary>Opciones del proveedor OpenAI / compatible con OpenAI.</summary>
public sealed class OpenAIProviderOptions
{
    /// <summary>API key. Si es null se usa la variable de entorno <see cref="ApiKeyEnvironmentVariable"/>.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";

    /// <summary>URL base. Por defecto <c>https://api.openai.com/v1</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string? Organization { get; set; }

    public string? Project { get; set; }

    /// <summary>Nombre del proveedor (para el registro y la telemetría).</summary>
    public string Name { get; set; } = "openai";

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Envía <c>max_tokens</c> en lugar de <c>max_completion_tokens</c>
    /// (necesario en algunos servidores compatibles antiguos).
    /// </summary>
    public bool UseLegacyMaxTokens { get; set; }

    /// <summary>Permite no requerir API key (p. ej. Ollama local).</summary>
    public bool RequireApiKey { get; set; } = true;
}

/// <summary>
/// Proveedor OpenAI: Chat Completions, Responses, embeddings, imágenes, voz y transcripción.
/// <see cref="Chat"/> funciona con cualquier servidor compatible: Ollama, Groq, DeepSeek,
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

    /// <summary>Instancia por defecto (API key desde <c>OPENAI_API_KEY</c>).</summary>
    public static OpenAIProvider Default { get; } = new();

    /// <summary>Proveedor para un servidor compatible con OpenAI.</summary>
    public static OpenAIProvider Compatible(string name, string baseUrl, string? apiKey = null) => new(new OpenAIProviderOptions
    {
        Name = name,
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        RequireApiKey = apiKey is not null,
    });

    public string Name => _options.Name;

    /// <summary>Modelo sobre Chat Completions (<c>/chat/completions</c>), compatible con servidores tipo OpenAI.</summary>
    public ILanguageModel Chat(string modelId) => new OpenAIChatLanguageModel(modelId, this);

    /// <summary>
    /// Modelo sobre la API Responses (<c>/responses</c>), recomendada por OpenAI: admite
    /// herramientas de servidor (<see cref="OpenAITools"/>) y resúmenes de razonamiento.
    /// </summary>
    public ILanguageModel Responses(string modelId) => new OpenAIResponsesLanguageModel(modelId, this);

    /// <summary>
    /// Modelo por defecto del registro (<c>"openai:gpt-5"</c>): la API Responses en OpenAI y
    /// Chat Completions en servidores compatibles (cuando se cambia <c>BaseUrl</c>).
    /// </summary>
    public ILanguageModel LanguageModel(string modelId) =>
        _options.BaseUrl.StartsWith("https://api.openai.com", StringComparison.OrdinalIgnoreCase) ? Responses(modelId) : Chat(modelId);

    /// <summary>Atajo: <c>openai["gpt-5"]</c> (equivale a <see cref="LanguageModel"/>).</summary>
    public ILanguageModel this[string modelId] => LanguageModel(modelId);

    /// <summary>Modelo de imágenes, p. ej. <c>"gpt-image-1"</c>.</summary>
    public IImageModel Image(string modelId) => new OpenAIImageModel(modelId, this);

    public IImageModel ImageModel(string modelId) => Image(modelId);

    /// <summary>Modelo de voz, p. ej. <c>"gpt-4o-mini-tts"</c>.</summary>
    public ISpeechModel Speech(string modelId) => new OpenAISpeechModel(modelId, this);

    public ISpeechModel SpeechModel(string modelId) => Speech(modelId);

    /// <summary>Modelo de transcripción, p. ej. <c>"gpt-4o-transcribe"</c> o <c>"whisper-1"</c>.</summary>
    public ITranscriptionModel Transcription(string modelId) => new OpenAITranscriptionModel(modelId, this);

    public ITranscriptionModel TranscriptionModel(string modelId) => Transcription(modelId);

    /// <summary>Modelo de embeddings, p. ej. <c>"text-embedding-3-small"</c>.</summary>
    public IEmbeddingModel Embedding(string modelId, int? dimensions = null) => new OpenAIEmbeddingModel(modelId, dimensions, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    internal OpenAIProviderOptions Options => _options;

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal Uri Url(string path) => new($"{_options.BaseUrl.TrimEnd('/')}/{path}");

    internal IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string>? extra)
    {
        var key = _options.RequireApiKey
            ? ProviderHttp.LoadApiKey(_options.ApiKey, _options.ApiKeyEnvironmentVariable, "OpenAI")
            : _options.ApiKey ?? Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
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
