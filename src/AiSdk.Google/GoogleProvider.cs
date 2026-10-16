using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Google;

/// <summary>Opciones del proveedor Google (Gemini API).</summary>
public sealed class GoogleProviderOptions
{
    /// <summary>API key. Si es null se usa <c>GOOGLE_GENERATIVE_AI_API_KEY</c> o <c>GEMINI_API_KEY</c>.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    public string Name { get; set; } = "google";

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }
}

/// <summary>Proveedor Google Gemini (Gemini API de Google AI Studio).</summary>
public sealed class GoogleProvider : IProvider
{
    private readonly GoogleProviderOptions _options;

    public GoogleProvider(GoogleProviderOptions? options = null) => _options = options ?? new GoogleProviderOptions();

    public GoogleProvider(Action<GoogleProviderOptions> configure)
    {
        _options = new GoogleProviderOptions();
        configure(_options);
    }

    public static GoogleProvider Default { get; } = new();

    public string Name => _options.Name;

    /// <summary>Modelo Gemini, p. ej. <c>"gemini-2.5-flash"</c> o <c>"gemini-2.5-pro"</c>.</summary>
    public ILanguageModel Gemini(string modelId) => new GoogleGenerativeLanguageModel(modelId, this);

    public ILanguageModel LanguageModel(string modelId) => Gemini(modelId);

    public ILanguageModel this[string modelId] => Gemini(modelId);

    /// <summary>Embeddings, p. ej. <c>"gemini-embedding-001"</c>.</summary>
    /// <param name="modelId">Id del modelo.</param>
    /// <param name="outputDimensionality">Dimensiones del vector (opcional).</param>
    /// <param name="taskType">Tipo de tarea: <c>RETRIEVAL_DOCUMENT</c>, <c>RETRIEVAL_QUERY</c>, <c>SEMANTIC_SIMILARITY</c>...</param>
    public IEmbeddingModel Embedding(string modelId = "gemini-embedding-001", int? outputDimensionality = null, string? taskType = null) =>
        new GoogleEmbeddingModel(modelId, outputDimensionality, taskType, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    /// <summary>Generación de imágenes con Imagen, p. ej. <c>"imagen-4.0-generate-001"</c>.</summary>
    public IImageModel Image(string modelId) => new GoogleImageModel(modelId, this);

    public IImageModel ImageModel(string modelId) => Image(modelId);

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal Uri ModelUrl(string modelId, string method, string? query = null)
    {
        var model = modelId.StartsWith("models/", StringComparison.Ordinal) ? modelId : "models/" + modelId;
        return new Uri($"{_options.BaseUrl.TrimEnd('/')}/{model}:{method}{(query is null ? string.Empty : "?" + query)}");
    }

    internal static string ModelName(string modelId) => modelId.StartsWith("models/", StringComparison.Ordinal) ? modelId : "models/" + modelId;

    internal IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string>? extra)
    {
        var key = _options.ApiKey
                  ?? Environment.GetEnvironmentVariable("GOOGLE_GENERATIVE_AI_API_KEY")
                  ?? ProviderHttp.LoadApiKey(null, "GEMINI_API_KEY", "Google");
        yield return new("x-goog-api-key", key);
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

/// <summary>Herramientas integradas de Gemini (se ejecutan en los servidores de Google).</summary>
public static class GoogleTools
{
    /// <summary>Búsqueda con Google (grounding). Las fuentes llegan en <c>result.Sources</c>.</summary>
    public static Tool GoogleSearch() =>
        Tool.ProviderDefined("google", "google_search", new JsonObject { ["googleSearch"] = new JsonObject() });

    /// <summary>Ejecución de código Python en el servidor de Google.</summary>
    public static Tool CodeExecution() =>
        Tool.ProviderDefined("google", "code_execution", new JsonObject { ["codeExecution"] = new JsonObject() });

    /// <summary>Lectura de las URL que aparecen en el prompt.</summary>
    public static Tool UrlContext() =>
        Tool.ProviderDefined("google", "url_context", new JsonObject { ["urlContext"] = new JsonObject() });
}

/// <summary>Constructor tipado de <see cref="ProviderOptions"/> para Gemini.</summary>
public static class GoogleOptions
{
    /// <param name="thinkingBudget">Tokens de razonamiento (0 desactiva en modelos Flash; -1 = dinámico).</param>
    /// <param name="includeThoughts">Devuelve resúmenes del razonamiento.</param>
    /// <param name="thinkingLevel">Nivel de razonamiento (<c>low</c> / <c>high</c>) en modelos que lo admiten.</param>
    /// <param name="safetySettings">Pares categoría/umbral, p. ej. <c>("HARM_CATEGORY_HARASSMENT", "BLOCK_ONLY_HIGH")</c>.</param>
    public static ProviderOptions Create(
        int? thinkingBudget = null,
        bool? includeThoughts = null,
        string? thinkingLevel = null,
        IEnumerable<(string Category, string Threshold)>? safetySettings = null)
    {
        var obj = new JsonObject();
        if (thinkingBudget is not null || includeThoughts is not null || thinkingLevel is not null)
        {
            var thinking = new JsonObject();
            if (thinkingBudget is { } budget)
            {
                thinking["thinkingBudget"] = budget;
            }

            if (includeThoughts is { } include)
            {
                thinking["includeThoughts"] = include;
            }

            if (thinkingLevel is not null)
            {
                thinking["thinkingLevel"] = thinkingLevel;
            }

            obj["generationConfig"] = new JsonObject { ["thinkingConfig"] = thinking };
        }

        if (safetySettings is not null)
        {
            obj["safetySettings"] = new JsonArray(safetySettings
                .Select(s => (JsonNode?)new JsonObject { ["category"] = s.Category, ["threshold"] = s.Threshold })
                .ToArray());
        }

        return new ProviderOptions { ["google"] = obj };
    }
}
