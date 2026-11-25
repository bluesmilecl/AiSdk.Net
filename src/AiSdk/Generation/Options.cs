namespace AiSdk;

/// <summary>Parámetros comunes de llamada (temperatura, tokens, reintentos...).</summary>
public class CallSettings
{
    public int? MaxOutputTokens { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? TopK { get; init; }

    public double? PresencePenalty { get; init; }

    public double? FrequencyPenalty { get; init; }

    public IReadOnlyList<string>? StopSequences { get; init; }

    public int? Seed { get; init; }

    /// <summary>Reintentos ante errores transitorios (429, 5xx...). Por defecto 2.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Espera máxima que se respeta de un <c>Retry-After</c> del servidor antes de reintentar (por defecto 60 s). Si
    /// el servidor pide esperar más, no se reintenta y se lanza el error (<see cref="ApiCallException.RetryAfter"/>).
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    /// <summary>Servicios accesibles desde las herramientas mediante <see cref="ToolExecutionContext.Services"/>.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Estado arbitrario accesible desde las herramientas.</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>
    /// Callback de aprobación humana para herramientas con <see cref="Tool.NeedsApproval"/>. Si es null, la llamada
    /// no se ejecuta y el modelo recibe un error explícito ("requiere aprobación y no hay un manejador"), salvo que
    /// se active <see cref="DeferToolApprovals"/>.
    /// </summary>
    public Func<ToolApprovalRequest, ValueTask<bool>>? OnToolApproval { get; init; }

    /// <summary>
    /// Sin <see cref="OnToolApproval"/>, deja pendientes las llamadas que requieren aprobación en vez de rechazarlas
    /// (aprobación diferida, como <c>tool-approval-request</c> del AI SDK): el bucle se detiene, las llamadas quedan
    /// en <see cref="GenerateTextResult.PendingApprovals"/> y se resuelven en la siguiente llamada con
    /// <see cref="GenerateTextOptions.ToolApprovals"/> (o <see cref="AgentSession.SendApprovalsAsync"/>).
    /// </summary>
    public bool DeferToolApprovals { get; init; }

    /// <summary>
    /// Máximo de herramientas que se ejecutan a la vez cuando el modelo pide varias en un mismo paso (por defecto 8;
    /// 1 las ejecuta en orden). Limita el gasto y la carga cuando el modelo pide muchas llamadas de golpe.
    /// </summary>
    public int MaxParallelToolCalls { get; init; } = 8;

    /// <summary>
    /// Repara una llamada a herramienta inválida (JSON roto, entrada que no cumple el esquema
    /// o herramienta inexistente). Devuelve la llamada corregida o null para informar el error al modelo.
    /// Ver <see cref="ToolCallRepair.WithModel"/>.
    /// </summary>
    public Func<ToolCallRepairContext, ValueTask<ToolCallPart?>>? RepairToolCall { get; init; }

    /// <summary>(streaming) Se invoca con cada parte de contenido: texto, razonamiento, herramientas, fuentes.</summary>
    public Func<StreamPart, ValueTask>? OnChunk { get; init; }

    /// <summary>Se invoca cuando la generación falla (en streaming el error también llega como <see cref="ErrorPart"/>).</summary>
    public Func<Exception, ValueTask>? OnError { get; init; }

    /// <summary>Se invoca al terminar correctamente, con el resultado completo.</summary>
    public Func<GenerateTextResult, ValueTask>? OnFinish { get; init; }

    /// <summary>Se invoca si la operación se cancela, con los pasos completados hasta ese momento.</summary>
    public Func<IReadOnlyList<StepResult>, ValueTask>? OnAbort { get; init; }
}

/// <summary>Motivo por el que una llamada a herramienta necesita reparación.</summary>
public enum ToolCallRepairReason
{
    /// <summary>El modelo llamó a una herramienta que no existe.</summary>
    UnknownTool,

    /// <summary>La entrada no es JSON válido.</summary>
    InvalidJson,

    /// <summary>La entrada es JSON pero no cumple el esquema de la herramienta.</summary>
    InvalidInput,
}

/// <summary>Contexto entregado a <see cref="CallSettings.RepairToolCall"/>.</summary>
public sealed record ToolCallRepairContext(
    ToolCallPart ToolCall,
    ToolCallRepairReason Reason,
    string Error,
    IReadOnlyList<Tool> Tools,
    IReadOnlyList<ModelMessage> Messages)
{
    /// <summary>Herramienta destino (null si <see cref="Reason"/> es <see cref="ToolCallRepairReason.UnknownTool"/>).</summary>
    public Tool? Tool => Tools.FirstOrDefault(t => t.Name == ToolCall.ToolName);

    /// <summary>Cancelación de la generación en curso (la reparación debe respetarla).</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Solicitud de aprobación de una llamada a herramienta.</summary>
public sealed record ToolApprovalRequest(ToolCallPart ToolCall, Tool Tool);

/// <summary>
/// Decisión sobre una llamada que quedó pendiente de aprobación (ver <see cref="CallSettings.DeferToolApprovals"/>).
/// </summary>
/// <param name="ToolCallId">Id de la llamada (<see cref="ToolCallPart.ToolCallId"/>).</param>
/// <param name="Approved">True para ejecutarla; false para responder al modelo que se denegó.</param>
/// <param name="Reason">Motivo opcional de la denegación, que se informa al modelo.</param>
public sealed record ToolApprovalResponse(string ToolCallId, bool Approved, string? Reason = null);

/// <summary>Opciones de <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/> y <see cref="Ai.StreamText(GenerateTextOptions)"/>.</summary>
public sealed class GenerateTextOptions : CallSettings
{
    public required ILanguageModel Model { get; init; }

    /// <summary>Instrucciones de sistema.</summary>
    public string? System { get; init; }

    /// <summary>Prompt simple del usuario (se añade al final de <see cref="Messages"/>).</summary>
    public string? Prompt { get; init; }

    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    public IReadOnlyList<Tool>? Tools { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Limita las herramientas disponibles (por nombre) sin cambiar <see cref="Tools"/>.</summary>
    public IReadOnlyList<string>? ActiveTools { get; init; }

    /// <summary>Condición de parada del bucle de herramientas. Por defecto <c>StepCountIs(1)</c>.</summary>
    public StopCondition? StopWhen { get; init; }

    /// <summary>Salida estructurada opcional (ver <see cref="AiSdk.Output"/>).</summary>
    public Output? Output { get; init; }

    /// <summary>Permite modificar modelo, herramientas o mensajes antes de cada paso.</summary>
    public Func<PrepareStepContext, ValueTask<PrepareStepResult?>>? PrepareStep { get; init; }

    public Func<StepResult, ValueTask>? OnStepFinish { get; init; }

    /// <summary>
    /// Decisiones sobre las llamadas que quedaron pendientes de aprobación (<see cref="CallSettings.DeferToolApprovals"/>).
    /// <see cref="Messages"/> debe terminar en el mensaje del asistente con esas llamadas (opcionalmente seguido de
    /// resultados de herramientas). Antes de llamar al modelo se ejecutan las aprobadas; las denegadas y las que no
    /// tengan decisión se responden con un error. Los resultados forman parte de
    /// <see cref="StepResult.ResponseMessages"/> del primer paso.
    /// </summary>
    /// <remarks>
    /// Las aprobaciones se aplican a las llamadas de los mensajes que usted entrega: si el historial viene del cliente,
    /// valídelo (o guárdelo en el servidor, como <see cref="AgentSession"/>) para que no se aprueben llamadas inventadas.
    /// </remarks>
    public IReadOnlyList<ToolApprovalResponse>? ToolApprovals { get; init; }

    /// <summary>
    /// (streaming) Transforma el stream de cada paso antes de emitirlo, p. ej.
    /// <see cref="StreamTransforms.Smooth"/> para una salida palabra por palabra.
    /// </summary>
    public Func<IAsyncEnumerable<StreamPart>, IAsyncEnumerable<StreamPart>>? StreamTransform { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

public sealed record PrepareStepContext(
    int StepNumber,
    IReadOnlyList<StepResult> Steps,
    ILanguageModel Model,
    IReadOnlyList<ModelMessage> Messages);

/// <summary>Sobrescrituras aplicadas a un paso concreto.</summary>
public sealed record PrepareStepResult
{
    public ILanguageModel? Model { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    public IReadOnlyList<string>? ActiveTools { get; init; }

    public string? System { get; init; }

    /// <summary>Reemplaza los mensajes enviados en este paso (p. ej. para recortar el contexto).</summary>
    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }
}

/// <summary>Opciones de <see cref="Ai.GenerateObjectAsync{T}"/> y <see cref="Ai.StreamObject{T}"/>.</summary>
public sealed class GenerateObjectOptions : CallSettings
{
    public required ILanguageModel Model { get; init; }

    public string? System { get; init; }

    public string? Prompt { get; init; }

    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    public string? SchemaName { get; init; }

    public string? SchemaDescription { get; init; }

    public CancellationToken CancellationToken { get; init; }
}
