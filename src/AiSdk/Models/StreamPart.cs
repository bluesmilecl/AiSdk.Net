using System.Text.Json;

namespace AiSdk;

/// <summary>
/// Partes de un stream. Los modelos emiten el subconjunto marcado como
/// "modelo"; <see cref="Ai.StreamText(GenerateTextOptions)"/> añade las partes de ciclo de vida
/// (inicio/fin de paso, resultados de herramientas...) en su <c>FullStream</c>.
/// </summary>
public abstract record StreamPart;

// ---- Partes emitidas por los modelos ----

/// <summary>(modelo) Fragmento de texto. <paramref name="Id"/> identifica el bloque de texto.</summary>
public sealed record TextDeltaPart(string Id, string Delta) : StreamPart;

/// <summary>(modelo) Fragmento de razonamiento.</summary>
public sealed record ReasoningDeltaPart(string Id, string Delta) : StreamPart;

/// <summary>(modelo) Cierre de un bloque de razonamiento, con metadatos (p. ej. firma).</summary>
public sealed record ReasoningEndPart(string Id) : StreamPart
{
    public IReadOnlyDictionary<string, JsonElement>? ProviderMetadata { get; init; }
}

/// <summary>(modelo) Comienza a generarse la entrada de una herramienta.</summary>
public sealed record ToolInputStartPart(string ToolCallId, string ToolName) : StreamPart;

/// <summary>(modelo) Fragmento del JSON de entrada de una herramienta.</summary>
public sealed record ToolInputDeltaPart(string ToolCallId, string Delta) : StreamPart;

/// <summary>(modelo) Llamada a herramienta completa.</summary>
public sealed record ToolCallStreamPart(ToolCallPart ToolCall) : StreamPart;

/// <summary>(modelo) Fuente o cita.</summary>
public sealed record SourceStreamPart(SourcePart Source) : StreamPart;

/// <summary>(modelo) Parte de contenido completa que no tiene representación incremental (p. ej. bloques crudos del proveedor).</summary>
public sealed record ContentStreamPart(ContentPart Content) : StreamPart;

/// <summary>(modelo) Metadatos de la respuesta.</summary>
public sealed record ResponseMetadataPart(ResponseMetadata Metadata) : StreamPart;

/// <summary>(modelo) Fin de la respuesta del modelo.</summary>
public sealed record ModelFinishPart(FinishReason FinishReason, Usage Usage) : StreamPart;

/// <summary>Error durante el stream.</summary>
public sealed record ErrorPart(Exception Error) : StreamPart;

// ---- Partes de ciclo de vida (StreamText) ----

public sealed record StartPart : StreamPart;

public sealed record StartStepPart(int StepNumber) : StreamPart;

public sealed record TextStartPart(string Id) : StreamPart;

public sealed record TextEndPart(string Id) : StreamPart;

public sealed record ReasoningStartPart(string Id) : StreamPart;

/// <summary>
/// Resultado de una herramienta. Lo emite el bucle tras ejecutar herramientas locales,
/// o el propio modelo para herramientas de servidor (<see cref="ToolResultPart.ProviderExecuted"/>).
/// </summary>
public sealed record ToolResultStreamPart(ToolResultPart ToolResult) : StreamPart;

public sealed record FinishStepPart(StepResult Step) : StreamPart;

public sealed record FinishPart(FinishReason FinishReason, Usage TotalUsage) : StreamPart;

/// <summary>
/// Datos propios para la interfaz (partes <c>data-*</c> del protocolo de <c>useChat</c>),
/// emitidos desde una herramienta con <see cref="ToolExecutionContext.WriteData"/>.
/// </summary>
public sealed record DataStreamPart(string Type, System.Text.Json.Nodes.JsonNode? Data) : StreamPart
{
    /// <summary>Id opcional: un dato con el mismo id reemplaza al anterior en el cliente.</summary>
    public string? Id { get; init; }

    /// <summary>Si es true, el cliente lo recibe pero no lo guarda en el historial del mensaje.</summary>
    public bool Transient { get; init; }
}

/// <summary>La generación se canceló (<see cref="System.Threading.CancellationToken"/>).</summary>
public sealed record AbortPart : StreamPart;
