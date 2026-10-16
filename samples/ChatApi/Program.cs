// Backend ASP.NET Core compatible con `useChat` de AI SDK UI (React/Vue/Svelte).
//
//   dotnet run --project samples/ChatApi
//
// En el frontend (Next.js/Vite):
//   const { messages, sendMessage } = useChat({
//     transport: new DefaultChatTransport({ api: "http://localhost:5000/api/chat" }),
//   });

using System.ComponentModel;
using AiSdk;
using AiSdk.AspNetCore;
using AiSdk.Extensions.DependencyInjection;
using AiSdk.UI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAiSdk(ai => ai
    .AddAnthropic()                                   // ANTHROPIC_API_KEY
    .AddOpenAI()                                      // OPENAI_API_KEY
    .AddOpenAI(o =>                                   // Ollama local (compatible con OpenAI)
    {
        o.Name = "ollama";
        o.BaseUrl = "http://localhost:11434/v1";
        o.RequireApiKey = false;
    })
    .AddAlias("chat", "anthropic:claude-opus-5-5")
    .AddDefaultModel("chat")
    .AddAgent<WeatherAgent>());

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Chat con streaming en el protocolo UI Message Stream.
app.MapPost("/api/chat", (UIChatRequest request, WeatherAgent agent, HttpContext http) =>
{
    var stream = agent.Stream(new AgentCallOptions
    {
        Messages = UIMessageConverter.ToModelMessages(request.Messages),
        Services = http.RequestServices,
        CancellationToken = http.RequestAborted,
    });
    return stream.ToUIMessageStreamResult(onError: ex => ex.Message);
});

// Texto plano en streaming.
app.MapGet("/api/haiku", (string topic, ILanguageModel model, HttpContext http) =>
    Ai.StreamText(model, $"Escribe un haiku sobre {topic}", http.RequestAborted).ToTextStreamResult());

// Modelo elegido dinámicamente: /api/ask?model=ollama:llama3.2&q=hola
app.MapGet("/api/ask", async (string q, string? model, ProviderRegistry registry) =>
{
    var result = await Ai.GenerateTextAsync(registry.LanguageModel(model ?? "chat"), q);
    return Results.Ok(new { result.Text, result.TotalUsage });
});

app.Run();

/// <summary>Agente definido como clase, resuelto por inyección de dependencias.</summary>
public sealed class WeatherAgent(ILanguageModel model) : Agent
{
    protected override ILanguageModel Model => model;

    protected override string Instructions => "Eres un asistente meteorológico. Responde en español.";

    protected override IEnumerable<Tool> Tools =>
    [
        Tool.FromDelegate("get_weather", "Obtiene el clima de una ciudad",
            ([Description("Ciudad")] string city) => new { city, temperatureC = Random.Shared.Next(5, 30) }),
    ];

    protected override CallSettings Settings => new()
    {
        ProviderOptions = AiSdk.Anthropic.AnthropicOptions.Create(effort: "medium", refusalFallback: true),
    };
}
