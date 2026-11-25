using System.Text;
using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Bedrock;

/// <summary>Amazon Bedrock provider options.</summary>
public sealed class BedrockProviderOptions
{
    /// <summary>Region, e.g. <c>us-east-1</c>. If null, <c>AWS_REGION</c> or <c>AWS_DEFAULT_REGION</c> is used.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// Bedrock API key (Bearer token). If null, <c>AWS_BEARER_TOKEN_BEDROCK</c> is used;
    /// if that does not exist either, requests are signed with SigV4 using <see cref="Credentials"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Fixed credentials (if null, they are read from the AWS environment variables).</summary>
    public AwsCredentials? Credentials { get; set; }

    /// <summary>
    /// Dynamic credentials provider (IAM roles, SSO, the AWS SDK...). Takes precedence over
    /// <see cref="Credentials"/>.
    /// </summary>
    public Func<CancellationToken, ValueTask<AwsCredentials>>? CredentialsProvider { get; set; }

    /// <summary>Base URL (defaults to <c>https://bedrock-runtime.{region}.amazonaws.com</c>).</summary>
    public string? BaseUrl { get; set; }

    public string Name { get; set; } = "bedrock";

    public HttpClient? HttpClient { get; set; }

    /// <summary>Clock used for signing (for tests).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// Amazon Bedrock provider over the Converse API: a single format for Claude, Llama,
/// Mistral, Amazon Nova, Cohere and other Bedrock models.
/// </summary>
public sealed class BedrockProvider : IProvider
{
    private readonly BedrockProviderOptions _options;

    public BedrockProvider(BedrockProviderOptions? options = null) => _options = options ?? new BedrockProviderOptions();

    public BedrockProvider(Action<BedrockProviderOptions> configure)
    {
        _options = new BedrockProviderOptions();
        configure(_options);
    }

    public static BedrockProvider Default { get; } = new();

    public string Name => _options.Name;

    /// <summary>
    /// Bedrock model by its id or inference profile, e.g.
    /// <c>"us.anthropic.claude-sonnet-4-5-20250929-v1:0"</c> or <c>"amazon.nova-pro-v1:0"</c>.
    /// </summary>
    public ILanguageModel Converse(string modelId) => new BedrockConverseLanguageModel(modelId, this);

    public ILanguageModel LanguageModel(string modelId) => Converse(modelId);

    public ILanguageModel this[string modelId] => Converse(modelId);

    /// <summary>Embeddings: <c>amazon.titan-embed-text-v2:0</c> or <c>cohere.embed-multilingual-v3</c>.</summary>
    public IEmbeddingModel Embedding(string modelId = "amazon.titan-embed-text-v2:0", int? dimensions = null) =>
        new BedrockEmbeddingModel(modelId, dimensions, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal string Region =>
        _options.Region
        ?? Environment.GetEnvironmentVariable("AWS_REGION")
        ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
        ?? throw new AiSdkException(Resources.Strings.RegionMissing);

    internal Uri ModelUrl(string modelId, string action) =>
        new($"{(_options.BaseUrl ?? $"https://bedrock-runtime.{Region}.amazonaws.com").TrimEnd('/')}/model/{Uri.EscapeDataString(modelId)}/{action}");

    /// <summary>Creates the POST request with the JSON body and authenticates it (API key or SigV4).</summary>
    internal async Task<HttpRequestMessage> CreateRequestAsync(Uri url, JsonObject body, bool stream, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Accept", stream ? "application/vnd.amazon.eventstream" : "application/json");

        var apiKey = _options.ApiKey ?? Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK");
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            return request;
        }

        var credentials = _options.CredentialsProvider is not null
            ? await _options.CredentialsProvider(cancellationToken).ConfigureAwait(false)
            : _options.Credentials ?? AwsCredentials.FromEnvironment()
              ?? throw new AiSdkException(Resources.Strings.CredentialsMissing);
        AwsSigV4.Sign(request, bytes, Region, "bedrock", credentials, _options.Clock());
        return request;
    }
}
