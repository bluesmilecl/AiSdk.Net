using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiSdk.Anthropic;
using AiSdk.AspNetCore;
using AiSdk.Bedrock;
using AiSdk.Cohere;
using AiSdk.Google;
using AiSdk.Mcp;
using AiSdk.OpenAI;
using AiSdk.Workflows;
using Microsoft.AspNetCore.Builder;

namespace AiSdk.Tests;

/// <summary>Mensajes en inglés por defecto y en español con una cultura de interfaz "es*" (ensamblado satélite).</summary>
public class LocalizationTests
{
    [Theory]
    [InlineData("", "The AiSdk Cohere provider only offers reranking and embeddings.")]
    [InlineData("en-US", "The AiSdk Cohere provider only offers reranking and embeddings.")]
    [InlineData("es-CL", "El proveedor Cohere de AiSdk solo ofrece reranking y embeddings.")]
    [InlineData("es", "El proveedor Cohere de AiSdk solo ofrece reranking y embeddings.")]
    [InlineData("fr-FR", "The AiSdk Cohere provider only offers reranking and embeddings.")]
    public void MessagesFollowTheCurrentUICulture(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal(expected, Assert.Throws<NotSupportedException>(() => CohereProvider.Default.LanguageModel("x")).Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("", "The MCP server's response exceeds the limit of 1024 bytes.")]
    [InlineData("fr-FR", "The MCP server's response exceeds the limit of 1024 bytes.")]
    [InlineData("es-CL", "La respuesta del servidor MCP supera el límite de 1024 bytes.")]
    public async Task McpLimitMessagesFollowTheCurrentUICulture(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var handler = new ScriptedHttpHandler((_, body) => (string?)body?["method"] switch
            {
                "initialize" => ScriptedHttpHandler.Initialize(body),
                "tools/list" => ScriptedHttpHandler.Json($"{{\"jsonrpc\":\"2.0\",\"id\":{body!["id"]},\"result\":{{\"tools\":[],\"pad\":\"{new string('x', 4096)}\"}}}}"),
                _ => new HttpResponseMessage(HttpStatusCode.Accepted),
            });
            await using var client = await McpClient.ConnectAsync(
                new HttpMcpTransport(new Uri("https://mcp.example.com/mcp"), new HttpClient(handler)) { MaxMessageBytes = 1024 },
                cancellationToken: TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expected, error.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(null, "Events over HTTP are disabled: configure WorkflowEndpointOptions.CanSendEvent to decide who can send them.")]
    [InlineData("fr-FR", "Events over HTTP are disabled: configure WorkflowEndpointOptions.CanSendEvent to decide who can send them.")]
    [InlineData("es-CL", "Los eventos por HTTP están deshabilitados: configure WorkflowEndpointOptions.CanSendEvent para decidir quién puede enviarlos.")]
    public async Task EndpointProblemDetailsFollowTheRequestCulture(string? acceptLanguage, string expected)
    {
        // La aplicación elige el idioma de cada petición (aquí con Accept-Language): el detalle del 403 lo sigue.
        var builder = TestAuth.CreateBuilder();
        builder.Services.AddAiSdkWorkflows(w => w.AddWorkflow(Workflow.Create<string, string>("echo", (_, input) => Task.FromResult(input))));
        await using var app = builder.Build();
        app.UseRequestLocalization(o => o.SetDefaultCulture("en").AddSupportedCultures("en", "es-CL").AddSupportedUICultures("en", "es-CL"));
        app.MapWorkflows();
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/workflows/events/order.approval") { Content = TestAuth.Json("""{"approved":true}""") };
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        using var response = await TestAuth.Client(app, "ana").SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(expected, problem.GetProperty("detail").GetString());
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("", "Missing API key for 'groq': pass it in ApiKey or set ApiKeyEnvironmentVariable (OPENAI_API_KEY is only read with https://api.openai.com).")]
    [InlineData("en-US", "Missing API key for 'groq': pass it in ApiKey or set ApiKeyEnvironmentVariable (OPENAI_API_KEY is only read with https://api.openai.com).")]
    [InlineData("es-CL", "Falta la API key de 'groq': pásala en ApiKey o indica ApiKeyEnvironmentVariable (OPENAI_API_KEY solo se lee con https://api.openai.com).")]
    [InlineData("es", "Falta la API key de 'groq': pásala en ApiKey o indica ApiKeyEnvironmentVariable (OPENAI_API_KEY solo se lee con https://api.openai.com).")]
    public async Task ProviderErrorsFollowTheCurrentUICulture(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var groq = new OpenAIProvider(new OpenAIProviderOptions { Name = "groq", BaseUrl = "https://api.groq.com/openai/v1", HttpClient = new HttpClient(new FakeHandler()) });
            var error = await Assert.ThrowsAsync<AiSdkException>(() => groq.Chat("llama").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }, TestContext.Current.CancellationToken));
            Assert.Equal(expected, error.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("", "The tool 'web_search' belongs to 'anthropic' and was skipped for Gemini.", "Respond only with valid JSON, with no additional text.")]
    [InlineData("es-CL", "La herramienta 'web_search' es de 'anthropic' y se omitió en Gemini.", "Responde únicamente con JSON válido, sin texto adicional.")]
    public async Task WarningsAndModelInstructionsFollowTheCurrentUICulture(string culture, string expectedWarning, string expectedInstruction)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            // Advertencia para quien llama (Gemini omite una herramienta de servidor de otro proveedor).
            var gemini = new GoogleProvider(new GoogleProviderOptions
            {
                ApiKey = "k",
                HttpClient = new HttpClient(new FakeHandler().Json("""{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""")),
            });
            var result = await gemini.Gemini("gemini-2.5-flash").GenerateAsync(
                new LanguageModelRequest { Messages = [ModelMessage.User("x")], Tools = [AnthropicTools.WebSearch().ToDefinition()] },
                TestContext.Current.CancellationToken);
            Assert.Contains(expectedWarning, result.Warnings);

            // Instrucción enviada al modelo (Bedrock Converse pide JSON por instrucción de sistema).
            var handler = new FakeHandler().Json("""{"output":{"message":{"role":"assistant","content":[{"text":"{}"}]}},"stopReason":"end_turn","usage":{"inputTokens":1,"outputTokens":1}}""");
            var bedrock = new BedrockProvider(new BedrockProviderOptions { Region = "us-east-1", ApiKey = "k", HttpClient = new HttpClient(handler) });
            await bedrock.Converse("amazon.nova-lite-v1:0").GenerateAsync(
                new LanguageModelRequest { Messages = [ModelMessage.User("x")], ResponseFormat = ResponseFormat.Json() },
                TestContext.Current.CancellationToken);
            Assert.Equal(expectedInstruction, (string?)handler.Requests[0].Body["system"]![0]!["text"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
