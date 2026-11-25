// Backend ASP.NET Core compatible con `useChat` de AI SDK UI (React/Vue/Svelte).
//
//   dotnet run --project samples/ChatApi
//
// En el frontend (Next.js/Vite), con un token de demostración de appsettings.Development.json:
//   const { messages, sendMessage, addToolApprovalResponse } = useChat({
//     transport: new DefaultChatTransport({
//       api: "http://localhost:5008/api/chat",
//       headers: { Authorization: "Bearer dev-ana-4f1c2a9e7b3d4c58" },
//     }),
//     // Tras aprobar o denegar, reenvía la conversación para que el servidor continúe.
//     sendAutomaticallyWhen: lastAssistantMessageIsCompleteWithApprovalResponses,
//   });
//
// send_weather_alert requiere aprobación: su parte "tool-send_weather_alert" llega en el estado "approval-requested";
// muestre part.input y llame a addToolApprovalResponse({ id: part.approval.id, approved: true | false, reason }).

using System.ComponentModel;
using AiSdk;
using AiSdk.Artifacts;
using AiSdk.AspNetCore;
using AiSdk.Documents.Generation;
using AiSdk.Extensions.DependencyInjection;
using AiSdk.UI;
using AiSdk.Workflows;
using ChatApi;
using Microsoft.AspNetCore.Authentication;

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
            // Aprobación humana: POST /workflows/events/refund.approval?key={orderId} con {"approved": true},
            // solo de un usuario con el rol refund-approver distinto de quien pidió el reembolso (ver MapWorkflows).
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

// Autenticación: en desarrollo, tokens de demostración (DemoAuthentication, appsettings.Development.json).
// Reemplácela por la suya en producción. Los endpoints de AiSdk exigen un usuario autenticado por defecto.
builder.Services.AddAuthentication(DemoAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoAuthentication>(DemoAuthentication.SchemeName, null);
builder.Services.AddAuthorization();

// CORS: solo los orígenes del frontend configurados en Cors:AllowedOrigins (nunca AllowAnyOrigin).
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "POST", "DELETE")));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// El detalle de los errores (incluidos los de herramientas) solo se muestra en desarrollo.
string OnError(Exception ex) => app.Environment.IsDevelopment() ? ex.Message : "Se produjo un error.";

// Todo /api exige un usuario autenticado (consume tokens del modelo).
var api = app.MapGroup("/api").RequireAuthorization();

// Chat con streaming reanudable en el protocolo UI Message Stream.
// La generación sigue aunque el cliente se desconecte; al volver, useChat la retoma.
// El id de chat lo elige el cliente: el stream se guarda bajo ese id *y el usuario autenticado*, así que nadie
// puede reemplazar ni reanudar el chat de otro usuario. Las herramientas se ejecutan en el servidor: sus resultados
// reenviados por el navegador se descartan (podrían estar falsificados).
// send_weather_alert requiere aprobación: el stream la pide (approval-requested) con un approvalId firmado para este
// usuario, este chat y esa entrada exacta; al reanudar solo se ejecuta si la respuesta del navegador verifica.
api.MapPost("/chat", (UIChatRequest request, WeatherAgent agent, IResumableStreamStore store, HttpContext http) =>
{
    var chatId = request.Id ?? Guid.NewGuid().ToString("N");
    var approvals = ToolApprovalProtection.ForChat(http, chatId);
    var input = UIMessageConverter.ToModelInput(request.Messages, new UIMessageConverterOptions
    {
        ServerExecutedTools = WeatherAgent.ServerTools,
        ToolApprovals = approvals,
    });
    var stream = agent.Stream(new AgentCallOptions
    {
        Messages = input.Messages,
        ToolApprovals = input.ToolApprovals,   // aprobaciones verificadas: se ejecutan antes de llamar al modelo
        DeferToolApprovals = true,             // las nuevas quedan pendientes y se piden al usuario
        Services = http.RequestServices,
    });
    return stream.ToResumableUIMessageStreamResult(store, chatId, onError: OnError, toolApprovals: approvals);
});

// Agente de trabajo: crea artifacts (HTML, React, diagramas), documentos Word/PDF/Excel descargables y tareas programadas.
// El usuario sale de la autenticación (nunca del cuerpo) y es el límite de confianza: los artifacts y tareas son suyos.
// El id de conversación (elegido por el cliente) solo agrupa los artifacts dentro de su propio espacio.
api.MapPost("/workspace", (UIChatRequest request, WorkspaceAgent agent, HttpContext http) =>
    agent.Stream(new AgentCallOptions
    {
        Messages = UIMessageConverter.ToModelMessages(request.Messages),
        Services = http.RequestServices,
        Context = new Dictionary<string, object?>
        {
            ["conversationId"] = request.Id,
            ["userId"] = EndpointSecurity.GetUserId(http),
        },
    }).ToUIMessageStreamResult(onError: OnError));

// Artifacts: exigen autenticación y cada usuario solo ve los suyos (iframe con CSP sandbox; en producción,
// sírvalos desde otro dominio, p. ej. artifacts.example.com).
app.MapArtifacts("/artifacts");

// Workflows: por HTTP solo se inician reembolsos (agent-task es interno: lo programan los agentes) y cada usuario
// ve sus ejecuciones. La aprobación no se puede falsificar: exige el rol refund-approver y nadie aprueba su propio
// reembolso. Los POST exigen Content-Type: application/json.
app.MapWorkflows("/workflows", new WorkflowEndpointOptions
{
    CanStart = (_, workflow) => ValueTask.FromResult(workflow == "refund"),
    CanSendEvent = async (http, e) =>
    {
        if (e.EventName != "refund.approval" || !http.User.IsInRole("refund-approver") || e.WaitingRuns.Count == 0)
        {
            return false;
        }

        var engine = http.RequestServices.GetRequiredService<WorkflowEngine>();
        var approver = EndpointSecurity.GetUserId(http);
        foreach (var run in e.WaitingRuns)
        {
            if (await WorkflowEndpoints.GetRunOwnerAsync(engine, run, http.RequestAborted) == approver)
            {
                return false;
            }
        }

        return true;
    },
});

// Reanudación: useChat({ resume: true }) llama a GET /api/chat/{id}/stream al cargar la página (con las mismas cabeceras).
api.MapGet("/chat/{id}/stream", (string id, IResumableStreamStore store) => UIMessageStreamResults.Resume(store, id));

// Stream propio con datos (partes data-* en useChat) además del texto.
api.MapPost("/report", (UIChatRequest request, ILanguageModel model) =>
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
    }, onError: OnError));

// Texto plano en streaming.
api.MapGet("/haiku", (string topic, ILanguageModel model, HttpContext http) =>
    Ai.StreamText(model, $"Escribe un haiku sobre {topic}", http.RequestAborted).ToTextStreamResult());

// Modelo elegido dinámicamente: /api/ask?model=ollama:llama3.2&q=hola
api.MapGet("/ask", async (string q, string? model, ProviderRegistry registry) =>
{
    var result = await Ai.GenerateTextAsync(registry.LanguageModel(model ?? "chat"), q);
    return Results.Ok(new { result.Text, result.TotalUsage });
});

app.Run();
