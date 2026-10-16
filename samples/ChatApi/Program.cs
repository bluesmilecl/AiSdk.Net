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
using AiSdk.Artifacts;
using AiSdk.AspNetCore;
using AiSdk.Documents.Generation;
using AiSdk.Extensions.DependencyInjection;
using AiSdk.UI;
using AiSdk.Workflows;

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
    .AddFallback("chat", "anthropic:claude-opus-5-5", "openai:gpt-5")   // si Claude falla, usa GPT
    .AddGoogle()                                      // GEMINI_API_KEY
    .AddDefaultModel("chat")
    .AddResumableStreams()                            // useChat({ resume: true })
    .AddAgent<WeatherAgent>());

// Artifacts y archivos generados por los agentes (en disco para que sobrevivan reinicios).
builder.Services.AddSingleton<IArtifactStore>(new FileSystemArtifactStore(Path.Combine(builder.Environment.ContentRootPath, "data", "artifacts")));

// Workflows durables, automatizaciones y tareas programadas por los agentes.
builder.Services.AddAiSdkWorkflows(w =>
{
    w.UseFileSystemStore(Path.Combine(builder.Environment.ContentRootPath, "data", "workflows"));
    w.AddWorkflow(Workflow.Create<RefundRequest, string>("refund", async (ctx, refund) =>
    {
        var risk = await ctx.StepAsync("risk-check", _ => Task.FromResult(refund.Amount > 100 ? "alto" : "bajo"));
        if (risk == "alto")
        {
            // Aprobación humana: POST /workflows/events/refund.approval?key={orderId} con {"approved": true}
            var decision = await ctx.WaitForEventAsync<Approval>("refund.approval", refund.OrderId, TimeSpan.FromDays(2));
            if (decision is not { Approved: true }) return "rechazado";
        }

        await ctx.StepAsync("refund-payment", _ => Task.FromResult(true));   // llamada a la pasarela de pago
        return "reembolsado";
    }));
    w.AddAutomation(new Automation
    {
        Id = "nightly-cleanup",
        Name = "Limpieza nocturna",
        Trigger = new CronTrigger("0 3 * * *", "America/Santiago"),
        Workflow = AgentTasks.WorkflowName,
        Input = System.Text.Json.JsonSerializer.SerializeToElement(new AgentTaskInput("limpieza", "Resume los reembolsos rechazados de ayer.")),
    });
    w.ConfigureEngine((engine, sp) => engine.AddAgentTasks(new AgentTaskOptions
    {
        Agent = services => services!.GetRequiredService<WorkspaceAgent>(),
        OnCompleted = (result, _, _) =>
        {
            Console.WriteLine($"[tarea {result.Task}] {result.Text}");   // aquí: correo, Slack, notificación...
            return Task.CompletedTask;
        },
    }));
});
builder.Services.AddSingleton<WorkspaceAgent>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Chat con streaming reanudable en el protocolo UI Message Stream.
// La generación sigue aunque el cliente se desconecte; al volver, useChat la retoma.
app.MapPost("/api/chat", (UIChatRequest request, WeatherAgent agent, IResumableStreamStore store, HttpContext http) =>
{
    var stream = agent.Stream(new AgentCallOptions
    {
        Messages = UIMessageConverter.ToModelMessages(request.Messages),
        Services = http.RequestServices,
    });
    return stream.ToResumableUIMessageStreamResult(store, request.Id ?? Guid.NewGuid().ToString("N"), onError: ex => ex.Message);
});

// Agente de trabajo: crea artifacts (HTML, React, diagramas), documentos Word/PDF/Excel descargables y tareas programadas.
// El id de conversación y el usuario llegan a las herramientas por Context.
app.MapPost("/api/workspace", (UIChatRequest request, WorkspaceAgent agent, HttpContext http) =>
    agent.Stream(new AgentCallOptions
    {
        Messages = UIMessageConverter.ToModelMessages(request.Messages),
        Services = http.RequestServices,
        Context = new Dictionary<string, object?>
        {
            ["conversationId"] = request.Id,
            ["userId"] = http.User.Identity?.Name ?? "anonimo",
        },
    }).ToUIMessageStreamResult());

app.MapArtifacts("/artifacts");     // ver y descargar artifacts (iframe con CSP sandbox)
app.MapWorkflows("/workflows");     // iniciar workflows, eventos/aprobaciones, automatizaciones y webhooks

// Reanudación: useChat({ resume: true }) llama a GET /api/chat/{id}/stream al cargar la página.
app.MapGet("/api/chat/{id}/stream", (string id, IResumableStreamStore store) => UIMessageStreamResults.Resume(store, id));

// Stream propio con datos (partes data-* en useChat) además del texto.
app.MapPost("/api/report", (UIChatRequest request, ILanguageModel model) =>
    UIMessageStreamResults.Create(writer =>
    {
        writer.WriteData("status", new { step = "generando" }, id: "status");
        writer.Merge(Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Messages = UIMessageConverter.ToModelMessages(request.Messages),
            StreamTransform = StreamTransforms.Smooth(),
        }));
        return Task.CompletedTask;
    }));

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
            ([Description("Ciudad")] string city, ToolExecutionContext ctx) =>
            {
                ctx.WriteData("weather-progress", new { city, status = "consultando" }, transient: true);
                return new { city, temperatureC = Random.Shared.Next(5, 30) };
            }),
    ];

    protected override CallSettings Settings => new()
    {
        ProviderOptions = AiSdk.Anthropic.AnthropicOptions.Create(effort: "medium", refusalFallback: true),
    };
}

public sealed record RefundRequest(string OrderId, decimal Amount);

public sealed record Approval(bool Approved);

/// <summary>Agente con artifacts, generación de documentos y tareas programadas.</summary>
public sealed class WorkspaceAgent(ILanguageModel model, IArtifactStore artifacts, IServiceProvider services) : Agent
{
    private static readonly ArtifactToolOptions ArtifactOptions = new() { Url = a => $"/artifacts/{a.Id}" };

    protected override ILanguageModel Model => model;

    protected override string Instructions =>
        $"Eres un asistente de trabajo. Responde en español.\n{ArtifactTools.Instructions}\n{DocumentTools.Instructions}\n{AgentTasks.Instructions}";

    protected override IEnumerable<Tool> Tools =>
    [
        .. ArtifactTools.Create(artifacts, ArtifactOptions),
        .. DocumentTools.Create(new DocumentToolOptions { Store = artifacts, ArtifactOptions = ArtifactOptions }),
        .. AgentTasks.CreateTools(services.GetRequiredService<WorkflowEngine>(), new AgentTaskOptions { Agent = _ => this, DefaultTimeZone = "America/Santiago" }),
    ];
}
