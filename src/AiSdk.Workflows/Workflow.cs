using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSdk.Workflows;

/// <summary>Estado de una ejecución.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowStatus>))]
public enum WorkflowStatus
{
    Pending,
    Running,

    /// <summary>Durmiendo hasta <see cref="WorkflowRun.ResumeAt"/>.</summary>
    Sleeping,

    /// <summary>Esperando un evento externo (aprobación, webhook, respuesta de un usuario...).</summary>
    WaitingForEvent,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Resultado memorizado de un paso.</summary>
public sealed record StepRecord(string Name, JsonElement? Output = null, int Attempts = 1, DateTimeOffset CompletedAt = default)
{
    /// <summary>Para esperas de eventos: true si venció el plazo sin recibir el evento.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>Evento que espera una ejecución.</summary>
public sealed record EventWait(string EventName, string? Key = null, string StepKey = "", DateTimeOffset? TimeoutAt = null);

/// <summary>
/// Una ejecución de un workflow. Se persiste tras cada paso: si el proceso se reinicia, la ejecución
/// continúa desde el último paso completado.
/// </summary>
public sealed class WorkflowRun
{
    public required string Id { get; init; }

    public required string WorkflowName { get; init; }

    public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;

    public JsonElement Input { get; init; }

    public JsonElement? Output { get; set; }

    public string? Error { get; set; }

    /// <summary>Cuántas veces se ejecutó (o reanudó) el workflow.</summary>
    public int Executions { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Cuándo reanudar (sueño o vencimiento de una espera).</summary>
    public DateTimeOffset? ResumeAt { get; set; }

    public EventWait? Waiting { get; set; }

    /// <summary>Origen: <c>manual</c>, <c>cron:{id}</c>, <c>event:{nombre}</c>, <c>webhook:{id}</c>...</summary>
    public string? TriggeredBy { get; init; }

    public Dictionary<string, StepRecord> Steps { get; init; } = [];

    public List<string> Logs { get; init; } = [];

    /// <summary>
    /// Versión para concurrencia optimista: 0 si aún no se guardó. <see cref="IWorkflowStore.SaveRunAsync"/> solo
    /// guarda si coincide con la versión almacenada y la incrementa; si no, lanza <see cref="WorkflowConcurrencyException"/>.
    /// </summary>
    public long Version { get; set; }

    /// <summary>Instancia del motor (<see cref="WorkflowEngineOptions.InstanceId"/>) que tiene reclamada la ejecución.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// Hasta cuándo vale el reclamo de <see cref="LeaseOwner"/>. Mientras no venza, otras instancias no la ejecutan;
    /// si la instancia muere, otra la retoma al vencer.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public bool IsFinished => Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled;

    /// <summary>Deserializa la salida.</summary>
    public T? GetOutput<T>() => Output is { } o ? o.Deserialize<T>(JsonSchema.DefaultSerializerOptions) : default;

    internal WorkflowRun Clone() => JsonSerializer.Deserialize<WorkflowRun>(JsonSerializer.Serialize(this, JsonSchema.DefaultSerializerOptions), JsonSchema.DefaultSerializerOptions)!;
}

/// <summary>Definición de un workflow registrable en el <see cref="WorkflowEngine"/>.</summary>
public interface IWorkflow
{
    string Name { get; }

    string? Description { get; }

    Task<object?> ExecuteAsync(WorkflowContext context, JsonElement input);
}

/// <summary>
/// Workflow tipado definido como código (al estilo de Temporal, Inngest o Vercel Workflow).
/// El código debe ser determinista: todo efecto secundario (llamadas a modelos, APIs, correos...)
/// va dentro de <see cref="WorkflowContext.StepAsync{T}(string, Func{CancellationToken, Task{T}}, StepOptions?)"/>
/// para que al reanudar no se repita.
/// </summary>
public sealed class Workflow<TInput, TOutput>(string name, Func<WorkflowContext, TInput, Task<TOutput>> run, string? description = null) : IWorkflow
{
    public string Name => name;

    public string? Description => description;

    public async Task<object?> ExecuteAsync(WorkflowContext context, JsonElement input)
    {
        var typed = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default!
            : input.Deserialize<TInput>(JsonSchema.DefaultSerializerOptions)!;
        return await run(context, typed).ConfigureAwait(false);
    }
}

/// <summary>Fábrica de workflows.</summary>
public static class Workflow
{
    public static Workflow<TInput, TOutput> Create<TInput, TOutput>(string name, Func<WorkflowContext, TInput, Task<TOutput>> run, string? description = null) =>
        new(name, run, description);
}

/// <summary>Política de reintentos de un paso.</summary>
public sealed record StepOptions
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffFactor { get; init; } = 2;

    /// <summary>Tiempo máximo por intento.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Decide si una excepción se reintenta (por defecto todas salvo <see cref="NonRetryableException"/>).</summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    public static StepOptions Default { get; } = new();

    public static StepOptions NoRetry { get; } = new() { MaxAttempts = 1 };
}

/// <summary>
/// La ejecución o automatización cambió en el almacén desde que se leyó (otra réplica o proceso la modificó,
/// p. ej. al cancelarla). Quien la recibe debe volver a leerla en vez de sobrescribirla.
/// </summary>
public sealed class WorkflowConcurrencyException(string message) : InvalidOperationException(message);

/// <summary>Error que no debe reintentarse (datos inválidos, permisos...).</summary>
public sealed class NonRetryableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Señal interna: la ejecución se suspende (sueño o espera de evento) y se reanudará más tarde.</summary>
public sealed class WorkflowSuspendedException : Exception
{
    internal WorkflowSuspendedException(WorkflowStatus status, DateTimeOffset? resumeAt)
        : base("La ejecución del workflow quedó suspendida.")
    {
        Status = status;
        ResumeAt = resumeAt;
    }

    public WorkflowStatus Status { get; }

    public DateTimeOffset? ResumeAt { get; }
}
