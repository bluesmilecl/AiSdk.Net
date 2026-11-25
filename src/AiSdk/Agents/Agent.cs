using System.ComponentModel;
using System.Text.Json;
using AiSdk.Skills;

namespace AiSdk;

/// <summary>Common contract for agents.</summary>
public interface IAgent
{
    string Name { get; }

    Task<GenerateTextResult> GenerateAsync(AgentCallOptions options);

    StreamTextResult Stream(AgentCallOptions options);
}

/// <summary>Input for an agent call.</summary>
public sealed class AgentCallOptions
{
    public string? Prompt { get; init; }

    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    /// <summary>Gets or sets the services available to tools.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Gets or sets the state available to tools (e.g. the authenticated user's id).</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>Gets or sets an additional per-step callback (runs after the agent's own).</summary>
    public Func<StepResult, ValueTask>? OnStepFinish { get; init; }

    /// <summary>
    /// Gets or sets the approval handler for this call. It is used when the agent neither handles its own approvals
    /// (<c>HandlesApprovals</c>) nor has <see cref="CallSettings.OnToolApproval"/> in its settings. This is how
    /// sub-agents (<see cref="Agent.AsTool"/>) receive the handler of the agent that calls them.
    /// </summary>
    public Func<ToolApprovalRequest, ValueTask<bool>>? OnToolApproval { get; init; }

    /// <summary>Gets or sets the decisions on calls pending approval (see <see cref="GenerateTextOptions.ToolApprovals"/>).</summary>
    public IReadOnlyList<ToolApprovalResponse>? ToolApprovals { get; init; }

    /// <summary>
    /// Gets or sets whether approvals are deferred in this call even if the agent's settings do not defer them
    /// (<see cref="CallSettings.DeferToolApprovals"/>). Useful in a <c>useChat</c> endpoint, which is what can show
    /// the approval to the user. If there is a handler (<see cref="OnToolApproval"/> or the agent's), it decides.
    /// </summary>
    public bool DeferToolApprovals { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public static implicit operator AgentCallOptions(string prompt) => new() { Prompt = prompt };
}

/// <summary>
/// Base class for agents defined as classes (Laravel AI SDK style):
/// override <see cref="Model"/>, <see cref="Instructions"/>, <see cref="Tools"/>, etc.
/// Wraps the tool loop of <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class SupportAgent(ILanguageModel model) : Agent
/// {
///     protected override ILanguageModel Model => model;
///     protected override string Instructions => "You are a support agent.";
///     protected override IEnumerable&lt;Tool&gt; Tools => [Tool.FromDelegate("lookup_order", "Looks up an order", LookupOrder)];
/// }
/// </code>
/// </example>
public abstract class Agent : IAgent, IOrchestrationNode
{
    /// <summary>Gets the agent's name (defaults to the class name).</summary>
    public virtual string Name => GetType().Name;

    /// <summary>Gets the description used when the agent is exposed as a tool.</summary>
    public virtual string? Description => null;

    protected abstract ILanguageModel Model { get; }

    protected virtual string? Instructions => null;

    protected virtual IEnumerable<Tool> Tools => [];

    protected virtual ToolChoice? ToolChoice => null;

    /// <summary>Gets the stop condition. By default the agent stops after 20 steps.</summary>
    protected virtual StopCondition StopWhen => StopCondition.StepCountIs(20);

    protected virtual Output? Output => null;

    /// <summary>
    /// Gets the available skills: their catalog is added to the system prompt, along with the
    /// <c>load_skill</c>/<c>read_skill_file</c> tools (progressive disclosure).
    /// </summary>
    protected virtual SkillLibrary? Skills => null;

    protected virtual SkillToolOptions? SkillOptions => null;

    protected virtual CallSettings Settings => new();

    protected virtual ValueTask<PrepareStepResult?> PrepareStepAsync(PrepareStepContext context) => ValueTask.FromResult<PrepareStepResult?>(null);

    protected virtual ValueTask OnStepFinishAsync(StepResult stepResult) => ValueTask.CompletedTask;

    protected virtual ValueTask<bool> ApproveToolCallAsync(ToolApprovalRequest request) => ValueTask.FromResult(false);

    /// <summary>Gets whether the agent handles approvals (if not, they remain pending in the result).</summary>
    protected virtual bool HandlesApprovals => false;

    public Task<GenerateTextResult> GenerateAsync(AgentCallOptions options) => Ai.GenerateTextAsync(BuildOptions(options));

    public Task<GenerateTextResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default) =>
        GenerateAsync(new AgentCallOptions { Prompt = prompt, CancellationToken = cancellationToken });

    public Task<GenerateTextResult> GenerateAsync(IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default) =>
        GenerateAsync(new AgentCallOptions { Messages = messages, CancellationToken = cancellationToken });

    public StreamTextResult Stream(AgentCallOptions options) => Ai.StreamText(BuildOptions(options));

    public StreamTextResult Stream(string prompt, CancellationToken cancellationToken = default) =>
        Stream(new AgentCallOptions { Prompt = prompt, CancellationToken = cancellationToken });

    public StreamTextResult Stream(IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default) =>
        Stream(new AgentCallOptions { Messages = messages, CancellationToken = cancellationToken });

    /// <summary>Creates a session with conversation memory.</summary>
    public AgentSession CreateSession(string? conversationId = null, IConversationStore? store = null) =>
        new(this, conversationId ?? Guid.NewGuid().ToString("N"), store ?? new InMemoryConversationStore());

    /// <summary>
    /// Exposes the agent as a tool for another agent (sub-agents / multi-agent).
    /// The tool receives <c>{ "task": "..." }</c> and returns the sub-agent's final text.
    /// </summary>
    /// <remarks>
    /// Approvals for the sub-agent's tools are requested from the calling agent's handler (if the sub-agent has none of
    /// its own); without any handler, they are denied with an explicit error. If the sub-agent stops with unresolved
    /// calls (client tools or deferred approvals), the tool fails with an error instead of returning empty text.
    /// </remarks>
    public Tool AsTool(string? name = null, string? description = null) =>
        Tool.Create<SubAgentInput, string>(
            name ?? ToSnakeCase(Name),
            description ?? Description ?? $"Delega una tarea al agente {Name}.",
            async (input, ctx, ct) =>
            {
                var result = await GenerateAsync(new AgentCallOptions
                {
                    Prompt = input.Task,
                    Services = ctx.Services,
                    Context = ctx.Context,
                    OnToolApproval = ctx.OnToolApproval,
                    CancellationToken = ct,
                }).ConfigureAwait(false);
                EnsureCompleted(Name, result, includeClientTools: true);
                return result.Text;
            });

    /// <summary>Throws if the agent stopped with calls that nobody can resolve (e.g. inside a sub-agent).</summary>
    internal static void EnsureCompleted(string agentName, GenerateTextResult result, bool includeClientTools)
    {
        var pending = includeClientTools ? result.PendingToolCalls : result.PendingApprovals.Select(a => a.ToolCall).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"El agente '{agentName}' se detuvo con llamadas sin resolver ({string.Join(", ", pending.Select(c => c.ToolName))}): " +
                "requieren aprobación o son herramientas de cliente, y no se pueden reanudar desde aquí. Configure OnToolApproval en el agente principal o en este agente.");
        }
    }

    /// <summary>Takes part in orchestrations like any other node.</summary>
    Task<string> IOrchestrationNode.RunAsync(string input, OrchestrationContext context) => context.RunAgentAsync(this, input);

    internal GenerateTextOptions BuildOptions(AgentCallOptions call, IReadOnlyList<Tool>? extraTools = null, StopCondition? extraStop = null)
    {
        var s = Settings;
        var skills = Skills is { Count: > 0 } library ? library : null;
        return new GenerateTextOptions
        {
            Model = Model,
            System = skills is null ? Instructions : string.Join("\n\n", new[] { Instructions, skills.SystemPrompt() }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Prompt = call.Prompt,
            Messages = call.Messages,
            Tools = [.. Tools, .. skills is null ? [] : SkillTools.Create(skills, SkillOptions), .. extraTools ?? []],
            ToolChoice = ToolChoice,
            StopWhen = extraStop is null ? StopWhen : StopWhen | extraStop,
            Output = Output,
            PrepareStep = PrepareStepAsync,
            OnStepFinish = call.OnStepFinish is null
                ? OnStepFinishAsync
                : async step =>
                {
                    await OnStepFinishAsync(step).ConfigureAwait(false);
                    await call.OnStepFinish(step).ConfigureAwait(false);
                },
            OnToolApproval = HandlesApprovals ? ApproveToolCallAsync : s.OnToolApproval ?? call.OnToolApproval,
            DeferToolApprovals = s.DeferToolApprovals || call.DeferToolApprovals,
            ToolApprovals = call.ToolApprovals,
            MaxParallelToolCalls = s.MaxParallelToolCalls,
            MaxOutputTokens = s.MaxOutputTokens,
            Temperature = s.Temperature,
            TopP = s.TopP,
            TopK = s.TopK,
            PresencePenalty = s.PresencePenalty,
            FrequencyPenalty = s.FrequencyPenalty,
            StopSequences = s.StopSequences,
            Seed = s.Seed,
            MaxRetries = s.MaxRetries,
            MaxRetryAfter = s.MaxRetryAfter,
            Headers = s.Headers,
            ProviderOptions = s.ProviderOptions,
            RepairToolCall = s.RepairToolCall,
            OnChunk = s.OnChunk,
            OnError = s.OnError,
            OnFinish = s.OnFinish,
            OnAbort = s.OnAbort,
            Services = call.Services ?? s.Services,
            Context = call.Context ?? s.Context,
            CancellationToken = call.CancellationToken,
        };
    }

    private static string ToSnakeCase(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private sealed record SubAgentInput([property: Description("Descripción completa de la tarea a realizar.")] string Task);
}

/// <summary>Settings for <see cref="ToolLoopAgent"/>.</summary>
public sealed class AgentSettings : CallSettings
{
    public required ILanguageModel Model { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<Tool>? Tools { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Gets or sets the stop condition. Defaults to <c>StepCountIs(20)</c>.</summary>
    public StopCondition? StopWhen { get; init; }

    public Output? Output { get; init; }

    /// <summary>Gets or sets the agent's skills (catalog in the prompt and tools to load them).</summary>
    public SkillLibrary? Skills { get; init; }

    /// <summary>Gets or sets the options for the skill tools (e.g. the script executor).</summary>
    public SkillToolOptions? SkillOptions { get; init; }

    public Func<PrepareStepContext, ValueTask<PrepareStepResult?>>? PrepareStep { get; init; }

    public Func<StepResult, ValueTask>? OnStepFinish { get; init; }
}

/// <summary>
/// Agent configured through an object (equivalent to the AI SDK's <c>new ToolLoopAgent({...})</c>):
/// calls the model, runs tools and repeats until the stop condition is met.
/// </summary>
public class ToolLoopAgent(AgentSettings settings) : Agent
{
    public AgentSettings AgentSettings { get; } = settings;

    public override string Name => AgentSettings.Name ?? "agent";

    public override string? Description => AgentSettings.Description;

    protected override ILanguageModel Model => AgentSettings.Model;

    protected override string? Instructions => AgentSettings.Instructions;

    protected override IEnumerable<Tool> Tools => AgentSettings.Tools ?? [];

    protected override ToolChoice? ToolChoice => AgentSettings.ToolChoice;

    protected override StopCondition StopWhen => AgentSettings.StopWhen ?? base.StopWhen;

    protected override Output? Output => AgentSettings.Output;

    protected override SkillLibrary? Skills => AgentSettings.Skills;

    protected override SkillToolOptions? SkillOptions => AgentSettings.SkillOptions;

    protected override CallSettings Settings => AgentSettings;

    protected override ValueTask<PrepareStepResult?> PrepareStepAsync(PrepareStepContext context) =>
        AgentSettings.PrepareStep?.Invoke(context) ?? base.PrepareStepAsync(context);

    protected override ValueTask OnStepFinishAsync(StepResult stepResult) =>
        AgentSettings.OnStepFinish?.Invoke(stepResult) ?? ValueTask.CompletedTask;
}
