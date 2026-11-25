using System.Text.Json;

namespace AiSdk;

/// <summary>
/// Parts of a stream. Models emit the subset marked as
/// "model"; <see cref="Ai.StreamText(GenerateTextOptions)"/> adds the lifecycle parts
/// (step start/finish, tool results...) in its <c>FullStream</c>.
/// </summary>
public abstract record StreamPart;

// ---- Partes emitidas por los modelos ----

/// <summary>(model) Text chunk. <paramref name="Id"/> identifies the text block.</summary>
public sealed record TextDeltaPart(string Id, string Delta) : StreamPart;

/// <summary>(model) Reasoning chunk.</summary>
public sealed record ReasoningDeltaPart(string Id, string Delta) : StreamPart;

/// <summary>(model) End of a reasoning block, with metadata (e.g. a signature).</summary>
public sealed record ReasoningEndPart(string Id) : StreamPart
{
    public IReadOnlyDictionary<string, JsonElement>? ProviderMetadata { get; init; }
}

/// <summary>(model) Generation of a tool's input begins.</summary>
public sealed record ToolInputStartPart(string ToolCallId, string ToolName) : StreamPart;

/// <summary>(model) Chunk of a tool's input JSON.</summary>
public sealed record ToolInputDeltaPart(string ToolCallId, string Delta) : StreamPart;

/// <summary>(model) Complete tool call.</summary>
public sealed record ToolCallStreamPart(ToolCallPart ToolCall) : StreamPart;

/// <summary>(model) Source or citation.</summary>
public sealed record SourceStreamPart(SourcePart Source) : StreamPart;

/// <summary>(model) Complete content part that has no incremental representation (e.g. raw provider blocks).</summary>
public sealed record ContentStreamPart(ContentPart Content) : StreamPart;

/// <summary>(model) Response metadata.</summary>
public sealed record ResponseMetadataPart(ResponseMetadata Metadata) : StreamPart;

/// <summary>(model) End of the model's response.</summary>
public sealed record ModelFinishPart(FinishReason FinishReason, Usage Usage) : StreamPart;

/// <summary>Error during the stream.</summary>
public sealed record ErrorPart(Exception Error) : StreamPart;

// ---- Partes de ciclo de vida (StreamText) ----

public sealed record StartPart : StreamPart;

public sealed record StartStepPart(int StepNumber) : StreamPart;

public sealed record TextStartPart(string Id) : StreamPart;

public sealed record TextEndPart(string Id) : StreamPart;

public sealed record ReasoningStartPart(string Id) : StreamPart;

/// <summary>
/// Result of a tool. Emitted by the loop after running local tools,
/// or by the model itself for server-side tools (<see cref="ToolResultPart.ProviderExecuted"/>).
/// </summary>
public sealed record ToolResultStreamPart(ToolResultPart ToolResult) : StreamPart;

/// <summary>
/// The call was left pending human approval (<see cref="CallSettings.DeferToolApprovals"/>): the loop stops
/// after this step. In the UI stream it becomes the <c>tool-approval-request</c> chunk (see
/// <see cref="UI.ToolApprovalProtector"/>).
/// </summary>
public sealed record ToolApprovalRequestStreamPart(ToolApprovalRequest Request) : StreamPart;

/// <summary>
/// The user denied a call that was pending approval (<see cref="GenerateTextOptions.ToolApprovals"/>).
/// <paramref name="ToolResult"/> is the error result the model receives. In the UI stream it becomes the
/// <c>tool-output-denied</c> chunk.
/// </summary>
public sealed record ToolOutputDeniedStreamPart(ToolResultPart ToolResult) : StreamPart;

public sealed record FinishStepPart(StepResult Step) : StreamPart;

public sealed record FinishPart(FinishReason FinishReason, Usage TotalUsage) : StreamPart;

/// <summary>
/// Custom data for the UI (<c>data-*</c> parts of the <c>useChat</c> protocol),
/// emitted from a tool with <see cref="ToolExecutionContext.WriteData"/>.
/// </summary>
public sealed record DataStreamPart(string Type, System.Text.Json.Nodes.JsonNode? Data) : StreamPart
{
    /// <summary>Gets the optional id: a data part with the same id replaces the previous one on the client.</summary>
    public string? Id { get; init; }

    /// <summary>If true, the client receives it but does not save it in the message history.</summary>
    public bool Transient { get; init; }
}

/// <summary>The generation was canceled (<see cref="System.Threading.CancellationToken"/>).</summary>
public sealed record AbortPart : StreamPart;
