using System.ComponentModel;
using AiSdk.Anthropic;
using AiSdk.Bedrock;
using AiSdk.Cohere;
using AiSdk.Google;
using AiSdk.Mistral;
using AiSdk.OpenAI;

namespace AiSdk.IntegrationTests;

public sealed record CityWeather([property: Description("Ciudad")] string City, [property: Description("Temperatura en °C")] int TemperatureC);

public sealed record CityQuery([property: Description("Nombre de la ciudad")] string City);

/// <summary>
/// Pruebas contra las APIs reales. Cada proveedor se omite si su API key no está configurada.
/// Los modelos se pueden cambiar con variables de entorno AISDK_TEST_*_MODEL.
/// </summary>
public class LiveProviderTests
{
    public static IEnumerable<object[]> Providers() =>
    [
        ["anthropic"],
        ["openai"],
        ["google"],
        ["mistral"],
        ["bedrock"],
    ];

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    internal static bool Has(string name) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    internal static ILanguageModel Model(string provider)
    {
        switch (provider)
        {
            case "anthropic":
                Skip.IfNot(Has("ANTHROPIC_API_KEY"), "ANTHROPIC_API_KEY no configurada");
                return AnthropicProvider.Default.Messages(Env("AISDK_TEST_ANTHROPIC_MODEL", "claude-opus-5-5"));
            case "openai":
                Skip.IfNot(Has("OPENAI_API_KEY"), "OPENAI_API_KEY no configurada");
                return OpenAIProvider.Default.Responses(Env("AISDK_TEST_OPENAI_MODEL", "gpt-5"));
            case "google":
                Skip.IfNot(Has("GEMINI_API_KEY") || Has("GOOGLE_GENERATIVE_AI_API_KEY"), "GEMINI_API_KEY no configurada");
                return GoogleProvider.Default.Gemini(Env("AISDK_TEST_GOOGLE_MODEL", "gemini-2.5-flash"));
            case "mistral":
                Skip.IfNot(Has("MISTRAL_API_KEY"), "MISTRAL_API_KEY no configurada");
                return MistralProvider.Default.Chat(Env("AISDK_TEST_MISTRAL_MODEL", "mistral-small-latest"));
            case "bedrock":
                Skip.IfNot((Has("AWS_BEARER_TOKEN_BEDROCK") || Has("AWS_ACCESS_KEY_ID")) && Has("AWS_REGION") && Has("AISDK_TEST_BEDROCK_MODEL"),
                    "Credenciales de Bedrock, AWS_REGION o AISDK_TEST_BEDROCK_MODEL no configurados");
                return BedrockProvider.Default.Converse(Env("AISDK_TEST_BEDROCK_MODEL", string.Empty));
            default:
                throw new ArgumentException(provider);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task GeneratesText(string provider)
    {
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = Model(provider),
            Prompt = "Responde solo con la palabra: listo",
            MaxOutputTokens = 2000,
        });

        Assert.Contains("listo", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.TotalUsage.InputTokens > 0);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task StreamsText(string provider)
    {
        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = Model(provider),
            Prompt = "Cuenta del 1 al 5 separados por comas.",
            MaxOutputTokens = 2000,
        });

        var chunks = new List<string>();
        await foreach (var chunk in stream.TextStream)
        {
            chunks.Add(chunk);
        }

        Assert.Contains("5", string.Concat(chunks));
        Assert.NotEqual(FinishReason.Error, await stream.FinishReason);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task RunsToolLoop(string provider)
    {
        var calls = new List<string>();
        var weather = Tool.Create<CityQuery, CityWeather>("get_weather", "Obtiene la temperatura actual de una ciudad", q =>
        {
            calls.Add(q.City);
            return new CityWeather(q.City, 17);
        });

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = Model(provider),
            Prompt = "Usa la herramienta get_weather para saber la temperatura en Santiago y dime el número.",
            Tools = [weather],
            StopWhen = StopCondition.StepCountIs(5),
            MaxOutputTokens = 2000,
        });

        Assert.NotEmpty(calls);
        Assert.Contains("17", result.Text);
    }

    [SkippableTheory]
    [MemberData(nameof(Providers))]
    public async Task GeneratesStructuredObjects(string provider)
    {
        var result = await Ai.GenerateObjectAsync<CityWeather>(new GenerateObjectOptions
        {
            Model = Model(provider),
            Prompt = "Inventa el clima de Valparaíso: ciudad y temperatura entera en °C.",
            MaxOutputTokens = 2000,
        });

        Assert.Contains("Valpara", result.Object.City);
    }

    [SkippableFact]
    public async Task CountsTokensWithAnthropic()
    {
        var tokens = await Ai.CountTokensAsync(new GenerateTextOptions { Model = Model("anthropic"), Prompt = "Hola, ¿cómo estás?" });
        Assert.InRange(tokens, 1, 100);
    }

    [SkippableFact]
    public async Task CohereReranks()
    {
        Skip.IfNot(Has("COHERE_API_KEY"), "COHERE_API_KEY no configurada");
        var result = await Ai.RerankAsync(CohereProvider.Default.Reranking(), "capital de Chile", ["Lima es la capital de Perú", "Santiago es la capital de Chile"]);
        Assert.Equal(1, result.Ranking[0].OriginalIndex);
    }

    [SkippableFact]
    public async Task OpenAIEmbeds()
    {
        Skip.IfNot(Has("OPENAI_API_KEY"), "OPENAI_API_KEY no configurada");
        var result = await Ai.EmbedManyAsync(OpenAIProvider.Default.Embedding("text-embedding-3-small"), ["gato", "perro", "factura"]);
        Assert.True(Ai.CosineSimilarity(result.Embeddings[0], result.Embeddings[1]) > Ai.CosineSimilarity(result.Embeddings[0], result.Embeddings[2]));
    }
}
