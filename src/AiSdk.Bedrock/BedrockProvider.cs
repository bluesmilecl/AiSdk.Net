using System.Text;
using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Bedrock;

/// <summary>Opciones del proveedor Amazon Bedrock.</summary>
public sealed class BedrockProviderOptions
{
    /// <summary>Región, p. ej. <c>us-east-1</c>. Si es null se usa <c>AWS_REGION</c> o <c>AWS_DEFAULT_REGION</c>.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// API key de Bedrock (token Bearer). Si es null se usa <c>AWS_BEARER_TOKEN_BEDROCK</c>;
    /// si tampoco existe, se firma con SigV4 usando <see cref="Credentials"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Credenciales fijas (si es null se leen de las variables de entorno de AWS).</summary>
    public AwsCredentials? Credentials { get; set; }

    /// <summary>
    /// Proveedor de credenciales dinámicas (roles de IAM, SSO, el SDK de AWS...). Tiene prioridad sobre
    /// <see cref="Credentials"/>.
    /// </summary>
    public Func<CancellationToken, ValueTask<AwsCredentials>>? CredentialsProvider { get; set; }

    /// <summary>URL base (por defecto <c>https://bedrock-runtime.{region}.amazonaws.com</c>).</summary>
    public string? BaseUrl { get; set; }

    public string Name { get; set; } = "bedrock";

    public HttpClient? HttpClient { get; set; }

    /// <summary>Reloj usado para firmar (para pruebas).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// Proveedor Amazon Bedrock sobre la API Converse: un mismo formato para Claude, Llama,
/// Mistral, Amazon Nova, Cohere y otros modelos de Bedrock.
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
    /// Modelo de Bedrock por su id o perfil de inferencia, p. ej.
    /// <c>"us.anthropic.claude-sonnet-4-5-20250929-v1:0"</c> o <c>"amazon.nova-pro-v1:0"</c>.
    /// </summary>
    public ILanguageModel Converse(string modelId) => new BedrockConverseLanguageModel(modelId, this);

    public ILanguageModel LanguageModel(string modelId) => Converse(modelId);

    public ILanguageModel this[string modelId] => Converse(modelId);

    /// <summary>Embeddings: <c>amazon.titan-embed-text-v2:0</c> o <c>cohere.embed-multilingual-v3</c>.</summary>
    public IEmbeddingModel Embedding(string modelId = "amazon.titan-embed-text-v2:0", int? dimensions = null) =>
        new BedrockEmbeddingModel(modelId, dimensions, this);

    public IEmbeddingModel EmbeddingModel(string modelId) => Embedding(modelId);

    internal HttpClient Http => _options.HttpClient ?? ProviderHttp.DefaultClient;

    internal string Region =>
        _options.Region
        ?? Environment.GetEnvironmentVariable("AWS_REGION")
        ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
        ?? throw new AiSdkException("Falta la región de Bedrock. Indícala en las opciones o con AWS_REGION.");

    internal Uri ModelUrl(string modelId, string action) =>
        new($"{(_options.BaseUrl ?? $"https://bedrock-runtime.{Region}.amazonaws.com").TrimEnd('/')}/model/{Uri.EscapeDataString(modelId)}/{action}");

    /// <summary>Crea la petición POST con el cuerpo JSON y la autentica (API key o SigV4).</summary>
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
              ?? throw new AiSdkException("Faltan credenciales de Bedrock: usa ApiKey/AWS_BEARER_TOKEN_BEDROCK o AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY.");
        AwsSigV4.Sign(request, bytes, Region, "bedrock", credentials, _options.Clock());
        return request;
    }
}
