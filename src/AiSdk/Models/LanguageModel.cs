using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AiSdk;

/// <summary>
/// Contrato que implementa cada proveedor (equivalente a la especificación
/// <c>LanguageModelV2</c> del AI SDK). Las funciones de alto nivel
/// (<see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/>, agentes, etc.) solo dependen de esta interfaz.
/// </summary>
public interface ILanguageModel
{
    /// <summary>Identificador del proveedor, p. ej. <c>"openai"</c> o <c>"anthropic"</c>.</summary>
    string Provider { get; }

    /// <summary>Identificador del modelo, p. ej. <c>"gpt-5"</c>.</summary>
    string ModelId { get; }

    Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Petición de bajo nivel enviada a un <see cref="ILanguageModel"/>.</summary>
public sealed record LanguageModelRequest
{
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    public ResponseFormat? ResponseFormat { get; init; }

    public int? MaxOutputTokens { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? TopK { get; init; }

    public double? PresencePenalty { get; init; }

    public double? FrequencyPenalty { get; init; }

    public IReadOnlyList<string>? StopSequences { get; init; }

    public int? Seed { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>Definición de herramienta enviada al modelo (sin la función de ejecución).</summary>
public sealed record ToolDefinition(string Name, string? Description, JsonElement InputSchema)
{
    /// <summary>Solicita validación estricta del esquema cuando el proveedor lo soporta.</summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Proveedor dueño de una herramienta de servidor (p. ej. <c>"anthropic"</c>).
    /// Null para herramientas de función normales.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>Definición cruda de la herramienta de servidor, que el proveedor envía tal cual.</summary>
    public JsonObject? ProviderDefinition { get; init; }

    public bool IsProviderTool => ProviderDefinition is not null;
}

/// <summary>Estrategia de selección de herramientas.</summary>
public sealed record ToolChoice
{
    private ToolChoice(ToolChoiceKind kind, string? toolName = null)
    {
        Kind = kind;
        ToolName = toolName;
    }

    public ToolChoiceKind Kind { get; }

    public string? ToolName { get; }

    public static ToolChoice Auto { get; } = new(ToolChoiceKind.Auto);

    public static ToolChoice None { get; } = new(ToolChoiceKind.None);

    public static ToolChoice Required { get; } = new(ToolChoiceKind.Required);

    public static ToolChoice Tool(string toolName) => new(ToolChoiceKind.Tool, toolName);
}

public enum ToolChoiceKind
{
    Auto,
    None,
    Required,
    Tool,
}

/// <summary>Formato de respuesta solicitado al modelo.</summary>
public sealed record ResponseFormat
{
    public static ResponseFormat Text { get; } = new() { Type = ResponseFormatType.Text };

    public ResponseFormatType Type { get; init; }

    /// <summary>Esquema JSON (opcional) para <see cref="ResponseFormatType.Json"/>.</summary>
    public JsonElement? Schema { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public static ResponseFormat Json(JsonElement? schema = null, string? name = null, string? description = null) =>
        new() { Type = ResponseFormatType.Json, Schema = schema, Name = name, Description = description };
}

public enum ResponseFormatType
{
    Text,
    Json,
}

/// <summary>
/// Opciones específicas por proveedor. La clave es el nombre del proveedor
/// (<c>"openai"</c>, <c>"anthropic"</c>...) y el valor un objeto JSON que se
/// fusiona en el cuerpo de la petición HTTP.
/// </summary>
public sealed class ProviderOptions : Dictionary<string, JsonObject>
{
    public ProviderOptions() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public JsonObject? For(string provider) => TryGetValue(provider, out var value) ? value : null;
}

/// <summary>Resultado de una llamada no-streaming a un modelo.</summary>
public sealed record LanguageModelResult
{
    public required IReadOnlyList<ContentPart> Content { get; init; }

    public FinishReason FinishReason { get; init; } = FinishReason.Unknown;

    public Usage Usage { get; init; } = Usage.Empty;

    public ResponseMetadata? Response { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement>? ProviderMetadata { get; init; }
}

public sealed record ResponseMetadata(string? Id, string? ModelId, DateTimeOffset Timestamp);

[JsonConverter(typeof(JsonStringEnumConverter<FinishReason>))]
public enum FinishReason
{
    Stop,
    Length,
    ContentFilter,
    ToolCalls,
    Error,
    Other,
    Unknown,
}

/// <summary>Consumo de tokens.</summary>
public sealed record Usage
{
    public static Usage Empty { get; } = new();

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public int? CachedInputTokens { get; init; }

    public int? TotalTokens => InputTokens is null && OutputTokens is null ? null : (InputTokens ?? 0) + (OutputTokens ?? 0);

    public static Usage operator +(Usage a, Usage b) => new()
    {
        InputTokens = Add(a.InputTokens, b.InputTokens),
        OutputTokens = Add(a.OutputTokens, b.OutputTokens),
        ReasoningTokens = Add(a.ReasoningTokens, b.ReasoningTokens),
        CachedInputTokens = Add(a.CachedInputTokens, b.CachedInputTokens),
    };

    private static int? Add(int? x, int? y) => x is null && y is null ? null : (x ?? 0) + (y ?? 0);
}
