using AiSdk.Http;

namespace AiSdk.OpenAI;

/// <summary>Opciones del proveedor OpenAI / compatible con OpenAI.</summary>
public sealed class OpenAIProviderOptions
{
    /// <summary>API key. Si es null se usa la variable de entorno <see cref="ApiKeyEnvironmentVariable"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Variable de entorno con la API key cuando <see cref="ApiKey"/> es null. Si no se indica, se usa
    /// <c>OPENAI_API_KEY</c> solo cuando <see cref="BaseUrl"/> es el endpoint oficial de OpenAI
    /// (<c>https://api.openai.com</c>): la clave de OpenAI nunca se envía a otros servidores (Ollama, Groq,
    /// OpenRouter...). Para un servidor compatible indique su propia variable, p. ej. <c>"GROQ_API_KEY"</c>.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

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

    /// <summary>True si <see cref="BaseUrl"/> es el endpoint oficial de OpenAI (<c>https://api.openai.com</c>).</summary>
    internal bool IsOfficialEndpoint =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.Equals(uri.IdnHost, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Valor de <c>tool_choice</c> para <see cref="ToolChoiceKind.Required"/> (Mistral usa <c>"any"</c>).</summary>
    public string RequiredToolChoice { get; set; } = "required";

    /// <summary>Nombre del parámetro de semilla (Mistral usa <c>"random_seed"</c>).</summary>
    public string SeedParameterName { get; set; } = "seed";

    /// <summary>Envía <c>stream_options.include_usage</c> en streaming (algunos servidores no lo aceptan).</summary>
    public bool SendStreamOptions { get; set; } = true;

    /// <summary>Envía <c>encoding_format: "float"</c> en embeddings.</summary>
    public bool SendEmbeddingEncodingFormat { get; set; } = true;
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

    /// <summary>
    /// Proveedor para un servidor compatible con OpenAI (Ollama, Groq, OpenRouter, vLLM...). Nunca lee
    /// <c>OPENAI_API_KEY</c>: pase la clave en <paramref name="apiKey"/> o el nombre de su variable de entorno en
    /// <paramref name="apiKeyEnvironmentVariable"/> (sin ninguna, las peticiones van sin <c>Authorization</c>).
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
        _options.IsOfficialEndpoint ? Responses(modelId) : Chat(modelId);

    /// <summary>Atajo: <c>openai["gpt-5"]</c> (equivale a <see cref="LanguageModel"/>).</summary>
    public ILanguageModel this[string modelId] => LanguageModel(modelId);

    /// <summary>Modelo de imágenes, p. ej. <c>"gpt-image-1"</c>.</summary>
    public IImageModel Image(string modelId) => new OpenAIImageModel(modelId, this);

    public IImageModel ImageModel(string modelId) => Image(modelId);

    /// <summary>Modelo de voz, p. ej. <c>"gpt-4o-mini-tts"</c>.</summary>
    public ISpeechModel Speech(string modelId) => new OpenAISpeechModel(modelId, this);

    public ISpeechModel SpeechModel(string modelId) => Speech(modelId);

    /// <summary>Files API (<c>/files</c>).</summary>
    public OpenAIFileClient Files => new(this);

    /// <summary>Vector stores para la herramienta <see cref="OpenAITools.FileSearch"/>.</summary>
    public OpenAIVectorStoreClient VectorStores => new(this);

    /// <summary>Batch API para el modelo indicado (por defecto sobre la API Responses).</summary>
    public OpenAIBatchClient Batches(string modelId, bool useResponsesApi = true) => new(this, modelId, useResponsesApi);

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
