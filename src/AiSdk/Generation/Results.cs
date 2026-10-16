namespace AiSdk;

/// <summary>Resultado de un paso (una llamada al modelo + ejecución de herramientas).</summary>
public sealed record StepResult
{
    public required int StepNumber { get; init; }

    /// <summary>Contenido generado por el modelo más los resultados de herramientas.</summary>
    public required IReadOnlyList<ContentPart> Content { get; init; }

    public required FinishReason FinishReason { get; init; }

    public required Usage Usage { get; init; }

    public required LanguageModelRequest Request { get; init; }

    public ResponseMetadata? Response { get; init; }

    /// <summary>Mensajes (assistant + tool) que este paso añadió a la conversación.</summary>
    public required IReadOnlyList<ModelMessage> ResponseMessages { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Text => string.Concat(Content.OfType<TextPart>().Select(p => p.Text));

    public string? ReasoningText => Content.OfType<ReasoningPart>().Any()
        ? string.Concat(Content.OfType<ReasoningPart>().Select(p => p.Text))
        : null;

    public IReadOnlyList<ToolCallPart> ToolCalls => Content.OfType<ToolCallPart>().ToList();

    public IReadOnlyList<ToolResultPart> ToolResults => Content.OfType<ToolResultPart>().ToList();

    /// <summary>Fuentes citadas en este paso.</summary>
    public IReadOnlyList<SourcePart> Sources => Content.OfType<SourcePart>().ToList();
}

/// <summary>Resultado de <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/> o de un agente.</summary>
public sealed class GenerateTextResult
{
    private readonly Lazy<object?>? _output;

    internal GenerateTextResult(IReadOnlyList<StepResult> steps, Lazy<object?>? output)
    {
        Steps = steps;
        _output = output;
    }

    public IReadOnlyList<StepResult> Steps { get; }

    private StepResult Last => Steps[^1];

    /// <summary>Texto del último paso.</summary>
    public string Text => Last.Text;

    public string? ReasoningText => Last.ReasoningText;

    public IReadOnlyList<ContentPart> Content => Last.Content;

    public IReadOnlyList<ToolCallPart> ToolCalls => Last.ToolCalls;

    public IReadOnlyList<ToolResultPart> ToolResults => Last.ToolResults;

    /// <summary>Llamadas del último paso que no tienen resultado (herramientas de cliente o pendientes de aprobación).</summary>
    public IReadOnlyList<ToolCallPart> PendingToolCalls =>
        ToolCalls.Where(c => !c.ProviderExecuted && ToolResults.All(r => r.ToolCallId != c.ToolCallId)).ToList();

    /// <summary>Fuentes citadas en todos los pasos.</summary>
    public IReadOnlyList<SourcePart> Sources => Steps.SelectMany(s => s.Sources).ToList();

    public FinishReason FinishReason => Last.FinishReason;

    /// <summary>Uso del último paso.</summary>
    public Usage Usage => Last.Usage;

    /// <summary>Uso acumulado de todos los pasos.</summary>
    public Usage TotalUsage => Steps.Aggregate(Usage.Empty, (acc, s) => acc + s.Usage);

    public ResponseMetadata? Response => Last.Response;

    /// <summary>Todos los mensajes generados (para añadir al historial de la conversación).</summary>
    public IReadOnlyList<ModelMessage> ResponseMessages => Steps.SelectMany(s => s.ResponseMessages).ToList();

    /// <summary>Salida estructurada (si se configuró <see cref="GenerateTextOptions.Output"/>).</summary>
    /// <exception cref="NoObjectGeneratedException">Si el texto final no cumple el esquema.</exception>
    public object? Output => _output is not null ? _output.Value : throw new InvalidOperationException("No se configuró una salida estructurada (Output).");

    public T GetOutput<T>() => (T)Output!;
}

/// <summary>Resultado de <see cref="Ai.GenerateObjectAsync{T}"/>.</summary>
public sealed record GenerateObjectResult<T>(T Object, FinishReason FinishReason, Usage Usage, ResponseMetadata? Response, string RawText);
