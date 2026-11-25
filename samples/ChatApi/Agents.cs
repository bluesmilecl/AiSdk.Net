using System.ComponentModel;
using AiSdk;
using AiSdk.Artifacts;
using AiSdk.Documents.Generation;
using AiSdk.Workflows;

namespace ChatApi;

/// <summary>Agente definido como clase, resuelto por inyección de dependencias.</summary>
public sealed class WeatherAgent(ILanguageModel model) : Agent
{
    protected override ILanguageModel Model => model;

    /// <summary>Herramientas que ejecuta el servidor: sus resultados reenviados por el navegador se descartan.</summary>
    public static readonly string[] ServerTools = ["get_weather", "send_weather_alert"];

    protected override string Instructions => "Eres un asistente meteorológico. Responde en español.";

    protected override IEnumerable<Tool> Tools =>
    [
        Tool.FromDelegate("get_weather", "Obtiene el clima de una ciudad",
            ([Description("Ciudad")] string city, ToolExecutionContext ctx) =>
            {
                ctx.WriteData("weather-progress", new { city, status = "consultando" }, transient: true);
                return new { city, temperatureC = Random.Shared.Next(5, 30) };
            }),

        // Envía un correo: el usuario debe aprobarlo en el chat (addToolApprovalResponse) antes de que se ejecute.
        Tool.FromDelegate("send_weather_alert", "Envía por correo una alerta del clima de una ciudad",
            ([Description("Correo del destinatario")] string to, [Description("Ciudad")] string city) =>
            {
                // Aquí iría el envío real (SMTP, SendGrid...). Solo se llega aquí con la entrada que el usuario aprobó.
                return new { sent = true, to, city };
            }).RequireApproval(),
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
