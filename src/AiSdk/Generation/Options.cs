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

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    /// <summary>Servicios accesibles desde las herramientas mediante <see cref="ToolExecutionContext.Services"/>.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Estado arbitrario accesible desde las herramientas.</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>
    /// Callback de aprobación humana para herramientas con <see cref="Tool.NeedsApproval"/>.
    /// Si es null, las llamadas que requieren aprobación quedan pendientes y el bucle se detiene.
    /// </summary>
    public Func<ToolApprovalRequest, ValueTask<bool>>? OnToolApproval { get; init; }
}

/// <summary>Solicitud de aprobación de una llamada a herramienta.</summary>
public sealed record ToolApprovalRequest(ToolCallPart ToolCall, Tool Tool);

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
