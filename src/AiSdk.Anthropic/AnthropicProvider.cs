using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Anthropic;

/// <summary>Anthropic provider options.</summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>API key. If null, <c>ANTHROPIC_API_KEY</c> is used.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyEnvironmentVariable { get; set; } = "ANTHROPIC_API_KEY";

    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1";

    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary><c>anthropic-beta</c> headers sent with every request.</summary>
    public List<string> Betas { get; set; } = [];

    public Dictionary<string, string> Headers { get; set; } = [];

    public HttpClient? HttpClient { get; set; }

    public string Name { get; set; } = "anthropic";

    /// <summary>Default <c>max_tokens</c> for non-streaming requests.</summary>
    public int DefaultMaxTokens { get; set; } = 16000;

    /// <summary>Default <c>max_tokens</c> for streaming requests.</summary>
    public int DefaultStreamingMaxTokens { get; set; } = 64000;

    /// <summary>
    /// Sends <c>eager_input_streaming: true</c> on tools when streaming is used,
    /// so that large inputs arrive as they are generated. Disable it if you use
    /// a proxy or platform that does not accept the field.
    /// </summary>
    public bool EagerInputStreaming { get; set; } = true;
}

/// <summary>Anthropic (Claude) provider over the Messages API.</summary>
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

    /// <summary>Default instance (API key from <c>ANTHROPIC_API_KEY</c>).</summary>
    public static AnthropicProvider Default { get; } = new();

    public string Name => _options.Name;

    /// <summary>Messages model, e.g. <c>"claude-opus-5-5"</c>.</summary>
    public ILanguageModel Messages(string modelId) => new AnthropicMessagesLanguageModel(modelId, this);

    public ILanguageModel LanguageModel(string modelId) => Messages(modelId);

    /// <summary>Shortcut: <c>anthropic["claude-opus-5-5"]</c>.</summary>
    public ILanguageModel this[string modelId] => Messages(modelId);

    /// <summary>Message Batches for the specified model.</summary>
    public AnthropicBatchClient Batches(string modelId) => new(this, modelId);

    /// <summary>Files API.</summary>
    public AnthropicFileClient Files => new(this);

    /// <summary>Skills API (<c>/v1/skills</c>): uploads custom skills for use in Claude's container.</summary>
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

/// <summary>Typed builder of <see cref="ProviderOptions"/> for Anthropic.</summary>
public static class AnthropicOptions
{
    /// <summary>
    /// Creates provider options for Claude.
    /// </summary>
    /// <param name="effort"><c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c> (<c>output_config.effort</c>).</param>
    /// <param name="thinkingDisplay">
    /// <c>summarized</c> to receive a readable summary of the reasoning (sends <c>thinking: {type: "adaptive", display}</c>).
    /// </param>
    /// <param name="cacheControl">Enables automatic prompt caching (top-level <c>cache_control</c>).</param>
    /// <param name="betas">Additional beta headers.</param>
    /// <param name="refusalFallback">
    /// Enables the server-side fallback (<c>fallbacks: "default"</c>, beta
    /// <c>server-side-fallback-2026-07-01</c>): if a safety classifier rejects the
    /// request, the API retries it with an alternative model. Claude API only.
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
