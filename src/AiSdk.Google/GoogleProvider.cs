using System.Globalization;
using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Google;

/// <summary>Google provider options (Gemini API).</summary>
public sealed class GoogleProviderOptions
{
    /// <summary>API key. If null, <c>GOOGLE_GENERATIVE_AI_API_KEY</c> or <c>GEMINI_API_KEY</c> is used.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    public string Name { get; set; } = "google";

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }
}

/// <summary>Google Gemini provider (Gemini API from Google AI Studio).</summary>
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

    /// <summary>Gemini model, e.g. <c>"gemini-2.5-flash"</c> or <c>"gemini-2.5-pro"</c>.</summary>
    public ILanguageModel Gemini(string modelId) => new GoogleGenerativeLanguageModel(modelId, this);

    public ILanguageModel LanguageModel(string modelId) => Gemini(modelId);

    public ILanguageModel this[string modelId] => Gemini(modelId);

    /// <summary>Embeddings, e.g. <c>"gemini-embedding-001"</c>.</summary>
    /// <param name="modelId">Model id.</param>
    /// <param name="outputDimensionality">Vector dimensions (optional).</param>
    /// <param name="taskType">Task type: <c>RETRIEVAL_DOCUMENT</c>, <c>RETRIEVAL_QUERY</c>, <c>SEMANTIC_SIMILARITY</c>...</param>
    public IEmbeddingModel Embedding(string modelId = "gemini-embedding-001", int? outputDimensionality = null, string? taskType = null) =>
        new GoogleEmbeddingModel(modelId, outputDimensionality, taskType, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    /// <summary>Image generation with Imagen, e.g. <c>"imagen-4.0-generate-001"</c>.</summary>
    public IImageModel Image(string modelId) => new GoogleImageModel(modelId, this);

    public IImageModel ImageModel(string modelId) => Image(modelId);

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal Uri ModelUrl(string modelId, string method, string? query = null)
    {
        var model = modelId.StartsWith("models/", StringComparison.Ordinal) ? modelId : "models/" + modelId;

        // Cada segmento se escapa y no se aceptan "." ni "..": un id de modelo no puede apuntar a otra ruta de la API.
        var segments = model.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.InvalidModelId, modelId), nameof(modelId));
        }

        var path = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri($"{_options.BaseUrl.TrimEnd('/')}/{path}:{method}{(query is null ? string.Empty : "?" + query)}");
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

/// <summary>Gemini built-in tools (executed on Google's servers).</summary>
public static class GoogleTools
{
    /// <summary>Google Search (grounding). The sources arrive in <c>result.Sources</c>.</summary>
    public static Tool GoogleSearch() =>
        Tool.ProviderDefined("google", "google_search", new JsonObject { ["googleSearch"] = new JsonObject() });

    /// <summary>Python code execution on Google's server.</summary>
    public static Tool CodeExecution() =>
        Tool.ProviderDefined("google", "code_execution", new JsonObject { ["codeExecution"] = new JsonObject() });

    /// <summary>Fetches the URLs that appear in the prompt.</summary>
    public static Tool UrlContext() =>
        Tool.ProviderDefined("google", "url_context", new JsonObject { ["urlContext"] = new JsonObject() });
}

/// <summary>Typed builder of <see cref="ProviderOptions"/> for Gemini.</summary>
public static class GoogleOptions
{
    /// <param name="thinkingBudget">Reasoning tokens (0 disables it on Flash models; -1 = dynamic).</param>
    /// <param name="includeThoughts">Returns reasoning summaries.</param>
    /// <param name="thinkingLevel">Reasoning level (<c>low</c> / <c>high</c>) on models that support it.</param>
    /// <param name="safetySettings">Category/threshold pairs, e.g. <c>("HARM_CATEGORY_HARASSMENT", "BLOCK_ONLY_HIGH")</c>.</param>
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
