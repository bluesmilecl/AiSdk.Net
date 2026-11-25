namespace AiSdk;

/// <summary>Common call settings (temperature, tokens, retries...).</summary>
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

    /// <summary>Gets or sets the number of retries on transient errors (429, 5xx...). Defaults to 2.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Gets or sets the maximum server <c>Retry-After</c> wait that is honored before retrying (defaults to 60 s). If
    /// the server asks to wait longer, no retry is made and the error is thrown
    /// (<see cref="ApiCallException.RetryAfter"/>).
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    /// <summary>Gets or sets the services available to tools through <see cref="ToolExecutionContext.Services"/>.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Gets or sets arbitrary state available to tools.</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>
    /// Gets or sets the human approval callback for tools with <see cref="Tool.NeedsApproval"/>. If null, the call
    /// is not executed and the model receives an explicit error (the tool requires approval and there is no handler),
    /// unless <see cref="DeferToolApprovals"/> is enabled.
    /// </summary>
    public Func<ToolApprovalRequest, ValueTask<bool>>? OnToolApproval { get; init; }

    /// <summary>
    /// Gets or sets whether, without <see cref="OnToolApproval"/>, calls that require approval are left pending instead of
    /// rejected (deferred approval, like the AI SDK's <c>tool-approval-request</c>): the loop stops, the calls remain
    /// in <see cref="GenerateTextResult.PendingApprovals"/> and are resolved in the next call with
    /// <see cref="GenerateTextOptions.ToolApprovals"/> (or <see cref="AgentSession.SendApprovalsAsync"/>). When streaming,
    /// a <see cref="ToolApprovalRequestStreamPart"/> is emitted, which <c>useChat</c> shows in the
    /// <c>approval-requested</c> state (see <see cref="UI.ToolApprovalProtector"/>).
    /// </summary>
    public bool DeferToolApprovals { get; init; }

    /// <summary>
    /// Gets or sets the maximum number of tools that run at once when the model requests several in the same step
    /// (defaults to 8; 1 runs them in order). Limits cost and load when the model requests many calls at once.
    /// </summary>
    public int MaxParallelToolCalls { get; init; } = 8;

    /// <summary>
    /// Gets or sets a callback that repairs an invalid tool call (broken JSON, input that does not conform to the
    /// schema or a nonexistent tool). It returns the corrected call, or null to report the error to the model. See
    /// <see cref="ToolCallRepair.WithModel"/>.
    /// </summary>
    public Func<ToolCallRepairContext, ValueTask<ToolCallPart?>>? RepairToolCall { get; init; }

    /// <summary>(streaming) Invoked with each content part: text, reasoning, tools, sources.</summary>
    public Func<StreamPart, ValueTask>? OnChunk { get; init; }

    /// <summary>Invoked when generation fails (when streaming, the error also arrives as an <see cref="ErrorPart"/>).</summary>
    public Func<Exception, ValueTask>? OnError { get; init; }

    /// <summary>Invoked on successful completion, with the full result.</summary>
    public Func<GenerateTextResult, ValueTask>? OnFinish { get; init; }

    /// <summary>Invoked if the operation is canceled, with the steps completed up to that point.</summary>
    public Func<IReadOnlyList<StepResult>, ValueTask>? OnAbort { get; init; }
}

/// <summary>Reason why a tool call needs repair.</summary>
public enum ToolCallRepairReason
{
    /// <summary>The model called a tool that does not exist.</summary>
    UnknownTool,

    /// <summary>The input is not valid JSON.</summary>
    InvalidJson,

    /// <summary>The input is JSON but does not conform to the tool's schema.</summary>
    InvalidInput,
}

/// <summary>Context passed to <see cref="CallSettings.RepairToolCall"/>.</summary>
public sealed record ToolCallRepairContext(
    ToolCallPart ToolCall,
    ToolCallRepairReason Reason,
    string Error,
    IReadOnlyList<Tool> Tools,
    IReadOnlyList<ModelMessage> Messages)
{
    /// <summary>Gets the target tool (null if <see cref="Reason"/> is <see cref="ToolCallRepairReason.UnknownTool"/>).</summary>
    public Tool? Tool => Tools.FirstOrDefault(t => t.Name == ToolCall.ToolName);

    /// <summary>Gets the cancellation token of the ongoing generation (the repair must honor it).</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Approval request for a tool call.</summary>
public sealed record ToolApprovalRequest(ToolCallPart ToolCall, Tool Tool);

/// <summary>
/// Decision on a call that was left pending approval (see <see cref="CallSettings.DeferToolApprovals"/>).
/// </summary>
/// <param name="ToolCallId">Id of the call (<see cref="ToolCallPart.ToolCallId"/>).</param>
/// <param name="Approved">True to execute it; false to tell the model that it was denied.</param>
/// <param name="Reason">Optional reason for the denial, which is reported to the model.</param>
public sealed record ToolApprovalResponse(string ToolCallId, bool Approved, string? Reason = null);

/// <summary>Options for <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/> and <see cref="Ai.StreamText(GenerateTextOptions)"/>.</summary>
public sealed class GenerateTextOptions : CallSettings
{
    public required ILanguageModel Model { get; init; }

    /// <summary>Gets or sets the system instructions.</summary>
    public string? System { get; init; }

    /// <summary>Gets or sets a simple user prompt (appended to the end of <see cref="Messages"/>).</summary>
    public string? Prompt { get; init; }

    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    public IReadOnlyList<Tool>? Tools { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    /// <summary>
    /// Gets or sets a restriction of the available tools (by name) without changing <see cref="Tools"/>.
    /// </summary>
    public IReadOnlyList<string>? ActiveTools { get; init; }

    /// <summary>Gets or sets the stop condition of the tool loop. Defaults to <c>StepCountIs(1)</c>.</summary>
    public StopCondition? StopWhen { get; init; }

    /// <summary>Gets or sets an optional structured output (see <see cref="AiSdk.Output"/>).</summary>
    public Output? Output { get; init; }

    /// <summary>Gets or sets a callback that can modify the model, tools or messages before each step.</summary>
    public Func<PrepareStepContext, ValueTask<PrepareStepResult?>>? PrepareStep { get; init; }

    public Func<StepResult, ValueTask>? OnStepFinish { get; init; }

    /// <summary>
    /// Gets or sets the decisions on calls that were left pending approval (<see cref="CallSettings.DeferToolApprovals"/>).
    /// <see cref="Messages"/> must end with the assistant message containing those calls (optionally followed by
    /// tool results). The approved calls are executed before calling the model; denied calls and calls without a
    /// decision are answered with an error. The results are part of the first step's
    /// <see cref="StepResult.ResponseMessages"/>.
    /// </summary>
    /// <remarks>
    /// Approvals apply to the calls in the messages you provide: if the history comes from the client,
    /// validate it (or store it on the server, as <see cref="AgentSession"/> does) so that fabricated calls are not approved.
    /// With <c>useChat</c>, <see cref="UI.UIMessageConverter.ToModelInput"/> only returns approvals whose
    /// <c>approvalId</c> was issued by the server for that same call (<see cref="UI.ToolApprovalProtector"/>). Approved
    /// calls are never repaired (<see cref="CallSettings.RepairToolCall"/>): they run with the approved input or fail.
    /// </remarks>
    public IReadOnlyList<ToolApprovalResponse>? ToolApprovals { get; init; }

    /// <summary>
    /// (streaming) Gets or sets a transform applied to each step's stream before it is emitted, e.g.
    /// <see cref="StreamTransforms.Smooth"/> for word-by-word output.
    /// </summary>
    public Func<IAsyncEnumerable<StreamPart>, IAsyncEnumerable<StreamPart>>? StreamTransform { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Agent running this loop; the loop span is then reported as <c>invoke_agent</c>. Set by <see cref="Agent"/>.</summary>
    internal Internal.TelemetryAgent? TelemetryAgent { get; init; }

    /// <summary>
    /// Public entry point reported as <c>invoke_{entrypoint}</c> when no agent runs the loop (e.g. <c>generate_object</c>).
    /// Defaults to <c>generate_text</c> or <c>stream_text</c>.
    /// </summary>
    internal string? TelemetryEntrypoint { get; init; }

    /// <summary>Conversation id reported as <c>gen_ai.conversation.id</c> (set by <see cref="AgentSession"/>).</summary>
    internal string? ConversationId { get; init; }
}

public sealed record PrepareStepContext(
    int StepNumber,
    IReadOnlyList<StepResult> Steps,
    ILanguageModel Model,
    IReadOnlyList<ModelMessage> Messages);

/// <summary>Overrides applied to a specific step.</summary>
public sealed record PrepareStepResult
{
    public ILanguageModel? Model { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    public IReadOnlyList<string>? ActiveTools { get; init; }

    public string? System { get; init; }

    /// <summary>Gets the messages that replace those sent in this step (e.g. to trim the context).</summary>
    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }
}

/// <summary>Options for <see cref="Ai.GenerateObjectAsync{T}"/> and <see cref="Ai.StreamObject{T}"/>.</summary>
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
