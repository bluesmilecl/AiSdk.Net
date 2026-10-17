using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Anthropic;

/// <summary>Opciones del proveedor Anthropic.</summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>API key. Si es null se usa <c>ANTHROPIC_API_KEY</c>.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyEnvironmentVariable { get; set; } = "ANTHROPIC_API_KEY";

    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1";

    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>Cabeceras <c>anthropic-beta</c> que se envían en todas las peticiones.</summary>
    public List<string> Betas { get; set; } = [];

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }

    public string Name { get; set; } = "anthropic";

    /// <summary><c>max_tokens</c> por defecto en peticiones sin streaming.</summary>
    public int DefaultMaxTokens { get; set; } = 16000;

    /// <summary><c>max_tokens</c> por defecto en peticiones con streaming.</summary>
    public int DefaultStreamingMaxTokens { get; set; } = 64000;

    /// <summary>
    /// Envía <c>eager_input_streaming: true</c> en las herramientas cuando se usa streaming,
    /// para que las entradas grandes lleguen a medida que se generan. Desactívalo si usas
    /// un proxy o plataforma que no acepte el campo.
    /// </summary>
    public bool EagerInputStreaming { get; set; } = true;
}

/// <summary>Proveedor Anthropic (Claude) sobre la Messages API.</summary>
public sealed class AnthropicProvider : IProvider
{
    private readonly AnthropicProviderOptions _options;

    public AnthropicProvider(AnthropicProviderOptions? options = null)
    {
        _options = options ?? new AnthropicProviderOptions();
    }

    public AnthropicProvider(Action<AnthropicProviderOptions> configure)
    {
        _options = new AnthropicProviderOptions();
        configure(_options);
    }

    /// <summary>Instancia por defecto (API key desde <c>ANTHROPIC_API_KEY</c>).</summary>
    public static AnthropicProvider Default { get; } = new();

    public string Name => _options.Name;

    /// <summary>Modelo de mensajes, p. ej. <c>"claude-opus-5-5"</c>.</summary>
    public ILanguageModel Messages(string modelId) => new AnthropicMessagesLanguageModel(modelId, this);

    public ILanguageModel LanguageModel(string modelId) => Messages(modelId);

    /// <summary>Atajo: <c>anthropic["claude-opus-5-5"]</c>.</summary>
    public ILanguageModel this[string modelId] => Messages(modelId);

    /// <summary>Message Batches para el modelo indicado.</summary>
    public AnthropicBatchClient Batches(string modelId) => new(this, modelId);

    /// <summary>Files API.</summary>
    public AnthropicFileClient Files => new(this);

    /// <summary>API de Skills (<c>/v1/skills</c>): sube skills propias para usarlas en el contenedor de Claude.</summary>
    public AnthropicSkillsClient Skills => new(this);

    internal AnthropicProviderOptions Options => _options;

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal Uri Url(string path) => new($"{_options.BaseUrl.TrimEnd('/')}/{path}");

    internal IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string>? extra, IEnumerable<string> betas)
    {
        yield return new("x-api-key", ProviderHttp.LoadApiKey(_options.ApiKey, _options.ApiKeyEnvironmentVariable, "Anthropic"));
        yield return new("anthropic-version", _options.ApiVersion);
        var allBetas = _options.Betas.Concat(betas).Distinct().ToList();
        if (allBetas.Count > 0)
        {
            yield return new("anthropic-beta", string.Join(",", allBetas));
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

/// <summary>Constructor tipado de <see cref="ProviderOptions"/> para Anthropic.</summary>
public static class AnthropicOptions
{
    /// <summary>
    /// Crea opciones de proveedor para Claude.
    /// </summary>
    /// <param name="effort"><c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> o <c>max</c> (<c>output_config.effort</c>).</param>
    /// <param name="thinkingDisplay">
    /// <c>summarized</c> para recibir un resumen legible del razonamiento (envía <c>thinking: {type: "adaptive", display}</c>).
    /// </param>
    /// <param name="cacheControl">Activa el caché automático de prompts (<c>cache_control</c> de nivel superior).</param>
    /// <param name="betas">Cabeceras beta adicionales.</param>
    /// <param name="refusalFallback">
    /// Activa el fallback del lado del servidor (<c>fallbacks: "default"</c>, beta
    /// <c>server-side-fallback-2026-07-01</c>): si un clasificador de seguridad rechaza la
    /// petición, la API la reintenta con un modelo alternativo. Solo en la API de Claude.
    /// </param>
    public static ProviderOptions Create(
        string? effort = null,
        string? thinkingDisplay = null,
        bool cacheControl = false,
        IEnumerable<string>? betas = null,
        bool refusalFallback = false)
    {
        var obj = new JsonObject();
        var allBetas = new List<string>(betas ?? []);
        if (refusalFallback)
        {
            obj["fallbacks"] = "default";
            allBetas.Add("server-side-fallback-2026-07-01");
        }

        if (effort is not null)
        {
            obj["output_config"] = new JsonObject { ["effort"] = effort };
        }

        if (thinkingDisplay is not null)
        {
            obj["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = thinkingDisplay };
        }

        if (cacheControl)
        {
            obj["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }

        if (allBetas.Count > 0)
        {
            obj["betas"] = new JsonArray(allBetas.Select(b => (JsonNode?)b).ToArray());
        }

        return new ProviderOptions { ["anthropic"] = obj };
    }
}
