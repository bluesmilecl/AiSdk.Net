using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiSdk.Resources;
using System.Globalization;

namespace AiSdk;

/// <summary>
/// Unit that can take part in an orchestration: an agent, a function or a nested orchestration.
/// <see cref="Agent"/> classes already implement this interface.
/// </summary>
public interface IOrchestrationNode
{
    string Name { get; }

    /// <summary>Gets what the node does (used by the router and the supervisor to choose).</summary>
    string? Description { get; }

    Task<string> RunAsync(string input, OrchestrationContext context);
}

/// <summary>Event emitted during an orchestration (for logging, metrics or the UI).</summary>
public abstract record OrchestrationEvent(string Node)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record NodeStartedEvent(string Node, string Input) : OrchestrationEvent(Node);

public sealed record NodeCompletedEvent(string Node, string Output, Usage Usage, TimeSpan Duration) : OrchestrationEvent(Node);

public sealed record RoutedEvent(string Node, string Route, string? Reason) : OrchestrationEvent(Node);

public sealed record HandoffEvent(string Node, string To, string? Reason) : OrchestrationEvent(Node);

public sealed record EvaluationEvent(string Node, int Iteration, bool Approved, string Feedback, int? Score) : OrchestrationEvent(Node);

/// <summary>Execution of an agent within an orchestration.</summary>
public sealed record AgentRun(string Agent, string Input, string Output, Usage Usage, TimeSpan Duration);

/// <summary>Execution options for an orchestration.</summary>
public sealed class OrchestrationOptions
{
    public IServiceProvider? Services { get; init; }

    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>
    /// Gets or sets a callback that receives each event (agent start/end, routing, handoffs, evaluations).
    /// </summary>
    public Func<OrchestrationEvent, ValueTask>? OnEvent { get; init; }

    /// <summary>
    /// Gets or sets the approval handler for orchestration agents that do not have their own. Without it, tools
    /// that require approval are denied with an explicit error.
    /// </summary>
    public Func<ToolApprovalRequest, ValueTask<bool>>? OnToolApproval { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Shared state of a run: records executed agents, token usage and events.</summary>
public sealed class OrchestrationContext
{
    private readonly ConcurrentQueue<AgentRun> _runs = new();

    public OrchestrationContext(OrchestrationOptions? options = null)
    {
        Options = options ?? new OrchestrationOptions();
    }

    public OrchestrationOptions Options { get; }

    public CancellationToken CancellationToken => Options.CancellationToken;

    public IReadOnlyList<AgentRun> Runs => _runs.ToList();

    public Usage TotalUsage => _runs.Aggregate(Usage.Empty, (acc, r) => acc + r.Usage);

    /// <summary>Gets or sets the last node that produced the response.</summary>
    public string? LastNode { get; set; }

    /// <summary>Gets or sets the final conversation (orchestrations with handoffs).</summary>
    public IReadOnlyList<ModelMessage>? Messages { get; set; }

    public ValueTask EmitAsync(OrchestrationEvent orchestrationEvent) =>
        Options.OnEvent?.Invoke(orchestrationEvent) ?? ValueTask.CompletedTask;

    /// <summary>Runs an agent, recording its usage and emitting events.</summary>
    public Task<string> RunAgentAsync(IAgent agent, string input) =>
        RunAgentAsync(agent, new AgentCallOptions { Prompt = input }, input);

    /// <summary>Runs an agent with messages and returns the full result.</summary>
    public async Task<GenerateTextResult> RunAgentResultAsync(IAgent agent, AgentCallOptions call, string inputLabel)
    {
        await EmitAsync(new NodeStartedEvent(agent.Name, inputLabel)).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var result = await agent.GenerateAsync(new AgentCallOptions
        {
            Prompt = call.Prompt,
            Messages = call.Messages,
            Services = call.Services ?? Options.Services,
            Context = call.Context ?? Options.Context,
            OnStepFinish = call.OnStepFinish,
            OnToolApproval = call.OnToolApproval ?? Options.OnToolApproval,
            CancellationToken = CancellationToken,
        }).ConfigureAwait(false);

        // Una orquestación no puede reanudarse: un agente detenido esperando aprobaciones es un error, no un texto vacío.
        Agent.EnsureCompleted(agent.Name, result, includeClientTools: false);
        Record(agent.Name, inputLabel, result.Text, result.TotalUsage, watch.Elapsed);
        await EmitAsync(new NodeCompletedEvent(agent.Name, result.Text, result.TotalUsage, watch.Elapsed)).ConfigureAwait(false);
        return result;
    }

    private async Task<string> RunAgentAsync(IAgent agent, AgentCallOptions call, string inputLabel) =>
        (await RunAgentResultAsync(agent, call, inputLabel).ConfigureAwait(false)).Text;

    /// <summary>Records a run performed outside <see cref="RunAgentAsync(IAgent, string)"/> (e.g. a direct model call).</summary>
    public void Record(string agent, string input, string output, Usage usage, TimeSpan duration) =>
        _runs.Enqueue(new AgentRun(agent, input, output, usage, duration));
}

/// <summary>Result of an orchestration.</summary>
public sealed record OrchestrationResult(string Text, IReadOnlyList<AgentRun> Runs, Usage TotalUsage)
{
    /// <summary>Gets the final conversation (orchestrations with handoffs).</summary>
    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    /// <summary>Gets the last agent that responded (handoffs and router).</summary>
    public string? FinalAgent { get; init; }
}

/// <summary>Base class for orchestrations. They are nodes, so they can be nested inside one another.</summary>
public abstract class Orchestration : IOrchestrationNode
{
    public virtual string Name => GetType().Name;

    public virtual string? Description { get; init; }

    /// <summary>Runs the orchestration and returns the text, the agent runs and the total usage.</summary>
    public async Task<OrchestrationResult> RunAsync(string input, OrchestrationOptions? options = null)
    {
        var context = new OrchestrationContext(options);
        var text = await RunAsync(input, context).ConfigureAwait(false);
        return new OrchestrationResult(text, context.Runs, context.TotalUsage) { Messages = context.Messages, FinalAgent = context.LastNode };
    }

    public Task<string> RunAsync(string input, OrchestrationContext context) => ExecuteAsync(input, context);

    protected abstract Task<string> ExecuteAsync(string input, OrchestrationContext context);

    /// <summary>Exposes the orchestration as a tool for another agent.</summary>
    public Tool AsTool(string? name = null, string? description = null) => OrchestrationNodes.AsTool(this, name, description);
}

/// <summary>Helpers for creating nodes.</summary>
public static class OrchestrationNodes
{
    /// <summary>Creates a node from a function (transformations, API calls, validations...).</summary>
    public static IOrchestrationNode Function(string name, Func<string, CancellationToken, Task<string>> run, string? description = null) =>
        new FunctionNode(name, description, run);

    /// <inheritdoc cref="Function(string, Func{string, CancellationToken, Task{string}}, string?)"/>
    public static IOrchestrationNode Function(string name, Func<string, string> run, string? description = null) =>
        new FunctionNode(name, description, (input, _) => Task.FromResult(run(input)));

    /// <summary>Creates a tool that runs the node with <c>{ "task": "..." }</c>.</summary>
    public static Tool AsTool(IOrchestrationNode node, string? name = null, string? description = null, OrchestrationContext? context = null) =>
        Tool.Create<TaskInput, string>(
            name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(node.Name),
            description ?? node.Description ?? string.Format(CultureInfo.CurrentCulture, Strings.Orchestration_DelegateDescription, node.Name),
            (input, ctx, ct) => node.RunAsync(input.Task, context ?? new OrchestrationContext(new OrchestrationOptions
            {
                Services = ctx.Services,
                Context = ctx.Context,
                OnToolApproval = ctx.OnToolApproval, // las aprobaciones llegan al manejador del agente que delega
                CancellationToken = ct,
            })));

    /// <summary>Gives a node a name and description (e.g. an orchestration used as a router route or a supervisor worker).</summary>
    public static IOrchestrationNode Named(this IOrchestrationNode node, string name, string? description = null) =>
        new NamedNode(name, description ?? node.Description, node);

    private sealed class NamedNode(string name, string? description, IOrchestrationNode inner) : IOrchestrationNode
    {
        public string Name => name;

        public string? Description => description;

        public Task<string> RunAsync(string input, OrchestrationContext context) => inner.RunAsync(input, context);
    }

    internal sealed record TaskInput([property: LocalizedDescription(typeof(Strings), nameof(Strings.Orchestration_Param_Task))] string Task);

    private sealed class FunctionNode(string name, string? description, Func<string, CancellationToken, Task<string>> run) : IOrchestrationNode
    {
        public string Name => name;

        public string? Description => description;

        public async Task<string> RunAsync(string input, OrchestrationContext context)
        {
            await context.EmitAsync(new NodeStartedEvent(name, input)).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var output = await run(input, context.CancellationToken).ConfigureAwait(false);
            await context.EmitAsync(new NodeCompletedEvent(name, output, Usage.Empty, watch.Elapsed)).ConfigureAwait(false);
            return output;
        }
    }
}

/// <summary>
/// Multi-agent orchestration patterns (see Anthropic's "Building effective agents" and the
/// AI SDK agent patterns): sequential, parallel, router, supervisor, handoffs and evaluator-optimizer.
/// </summary>
public static class Orchestrations
{
    /// <summary>Sequential: the output of each step is the input of the next one.</summary>
    public static SequentialOrchestration Sequential(params IOrchestrationNode[] steps) => new(steps);

    /// <summary>
    /// Parallel: all branches receive the same input; the results are combined or synthesized by an aggregator.
    /// </summary>
    public static ParallelOrchestration Parallel(IEnumerable<IOrchestrationNode> branches, IOrchestrationNode? aggregator = null) =>
        new(branches.ToList()) { Aggregator = aggregator };

    /// <summary>Router: a model classifies the input and sends it to the most suitable node.</summary>
    public static RouterOrchestration Router(ILanguageModel classifier, params IOrchestrationNode[] routes) => new(classifier, routes);

    /// <summary>Supervisor (orchestrator-workers): a model plans and delegates subtasks, in parallel when appropriate.</summary>
    public static SupervisorOrchestration Supervisor(ILanguageModel model, params IOrchestrationNode[] workers) => new(model, workers);

    /// <summary>Handoffs: agents hand the conversation over to each other (Swarm / OpenAI Agents style).</summary>
    public static HandoffOrchestration Handoffs(Agent start, params Agent[] others) => new(start, others);

    /// <summary>Evaluator-optimizer: generates, evaluates against criteria and revises until approved.</summary>
    public static EvaluatorOptimizerOrchestration EvaluatorOptimizer(IOrchestrationNode generator, ILanguageModel evaluator, string criteria) =>
        new(generator, evaluator, criteria);
}

/// <summary>Chain of steps.</summary>
public sealed class SequentialOrchestration(IReadOnlyList<IOrchestrationNode> steps) : Orchestration
{
    public override string Name => "sequential";

    /// <summary>
    /// Gets or sets whether each step also receives the original input in addition to the previous output.
    /// </summary>
    public bool IncludeOriginalInput { get; init; }

    /// <summary>
    /// Gets or sets a condition that stops the chain when an output meets it (e.g. a failed validation).
    /// </summary>
    public Func<string, string, bool>? StopWhen { get; init; }

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var current = input;
        foreach (var step in steps)
        {
            var stepInput = IncludeOriginalInput && !ReferenceEquals(current, input)
                ? string.Format(CultureInfo.CurrentCulture, Strings.Orchestration_SequentialStepInput, input, current)
                : current;
            current = await step.RunAsync(stepInput, context).ConfigureAwait(false);
            context.LastNode = step.Name;
            if (StopWhen?.Invoke(step.Name, current) == true)
            {
                break;
            }
        }

        return current;
    }
}

/// <summary>Parallel branches with aggregation.</summary>
public sealed class ParallelOrchestration(IReadOnlyList<IOrchestrationNode> branches) : Orchestration
{
    public override string Name => "parallel";

    /// <summary>
    /// Gets or sets the node that synthesizes the results (it receives the original task and all the responses).
    /// </summary>
    public IOrchestrationNode? Aggregator { get; init; }

    /// <summary>
    /// Gets or sets a model-free combination (used when there is no aggregator). By default, one section per branch.
    /// </summary>
    public Func<IReadOnlyList<(string Name, string Output)>, string>? Combine { get; init; }

    public int MaxConcurrency { get; init; } = 8;

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var outputs = await Task.WhenAll(branches.Select(async branch =>
        {
            await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                return (branch.Name, Output: await branch.RunAsync(input, context).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var combined = Combine?.Invoke(outputs) ?? string.Join("\n\n", outputs.Select(o => $"## {o.Name}\n\n{o.Output.Trim()}"));
        if (Aggregator is null)
        {
            return combined;
        }

        context.LastNode = Aggregator.Name;
        return await Aggregator.RunAsync(string.Format(CultureInfo.CurrentCulture, Strings.Orchestration_AggregatorInput, input, combined), context).ConfigureAwait(false);
    }
}

/// <summary>Classifies the input and routes it to the appropriate node.</summary>
public sealed class RouterOrchestration(ILanguageModel classifier, IReadOnlyList<IOrchestrationNode> routes) : Orchestration
{
    public override string Name => "router";

    /// <summary>Gets or sets additional instructions for the classifier.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets or sets the node used when the classifier does not choose a valid route.</summary>
    public IOrchestrationNode? Fallback { get; init; }

    private sealed record Decision(
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Router_Param_Route))] string Route,
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Router_Param_Reason))] string Reason);

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var catalog = string.Join("\n", routes.Select(r => $"- {r.Name}: {r.Description ?? Strings.Router_NoDescription}"));
        var watch = Stopwatch.StartNew();
        var decision = await Ai.GenerateObjectAsync<Decision>(new GenerateObjectOptions
        {
            Model = classifier,
            System = string.Format(CultureInfo.CurrentCulture, Strings.Router_System, catalog, Instructions),
            Prompt = input,
            CancellationToken = context.CancellationToken,
        }).ConfigureAwait(false);
        context.Record(Name, input, decision.Object.Route, decision.Usage, watch.Elapsed);

        var route = routes.FirstOrDefault(r => string.Equals(r.Name, decision.Object.Route.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? Fallback
                    ?? throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Strings.Router_UnknownRoute, decision.Object.Route));
        await context.EmitAsync(new RoutedEvent(Name, route.Name, decision.Object.Reason)).ConfigureAwait(false);
        context.LastNode = route.Name;
        return await route.RunAsync(input, context).ConfigureAwait(false);
    }
}

/// <summary>
/// A supervisor model breaks the task down and delegates to workers exposed as tools.
/// If the model requests several delegations in the same step, they run in parallel.
/// </summary>
public sealed class SupervisorOrchestration(ILanguageModel model, IReadOnlyList<IOrchestrationNode> workers) : Orchestration
{
    public static string DefaultInstructions => Strings.Supervisor_DefaultInstructions;

    public override string Name => "supervisor";

    public string Instructions { get; init; } = DefaultInstructions;

    /// <summary>Gets or sets the supervisor's own tools, in addition to the workers.</summary>
    public IReadOnlyList<Tool> Tools { get; init; } = [];

    public int MaxSteps { get; init; } = 15;

    public ProviderOptions? ProviderOptions { get; init; }

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var supervisor = new ToolLoopAgent(new AgentSettings
        {
            Name = Name,
            Model = model,
            Instructions = string.Format(CultureInfo.CurrentCulture, Strings.Supervisor_Instructions, Instructions, string.Join("\n", workers.Select(w => $"- {w.Name}: {w.Description}"))),
            Tools = [.. workers.Select(w => OrchestrationNodes.AsTool(w, context: context)), .. Tools],
            StopWhen = StopCondition.StepCountIs(MaxSteps),
            ProviderOptions = ProviderOptions,
        });
        context.LastNode = Name;
        return await context.RunAgentAsync(supervisor, input).ConfigureAwait(false);
    }
}

/// <summary>
/// Agents that hand the conversation over to each other. Each agent receives <c>transfer_to_*</c> tools
/// pointing to the others; when one is used, the next agent continues with the full history.
/// </summary>
public sealed class HandoffOrchestration : Orchestration
{
    private readonly Agent _start;
    private readonly Dictionary<string, Agent> _agents;

    public HandoffOrchestration(Agent start, IEnumerable<Agent> others)
    {
        _start = start;
        _agents = new[] { start }.Concat(others).ToDictionary(a => ToolName(a.Name), StringComparer.Ordinal);
    }

    public override string Name => "handoffs";

    /// <summary>Gets or sets the maximum number of handoffs before stopping (prevents cycles).</summary>
    public int MaxHandoffs { get; init; } = 8;

    /// <summary>Gets or sets who can hand off to whom. By default, everyone to everyone.</summary>
    public Func<string, string, bool>? CanHandoff { get; init; }

    private static string ToolName(string agentName) => "transfer_to_" + JsonNamingPolicy.SnakeCaseLower.ConvertName(agentName);

    private sealed record HandoffInput([property: LocalizedDescription(typeof(Strings), nameof(Strings.Handoff_Param_Reason))] string Reason);

    /// <summary>Continues an existing conversation (e.g. a chat) from the specified agent.</summary>
    public async Task<OrchestrationResult> RunAsync(IReadOnlyList<ModelMessage> messages, string? startAgent = null, OrchestrationOptions? options = null)
    {
        var context = new OrchestrationContext(options);
        var text = await ContinueAsync([.. messages], startAgent is null ? _start : _agents.Values.First(a => a.Name == startAgent), context).ConfigureAwait(false);
        return new OrchestrationResult(text, context.Runs, context.TotalUsage) { Messages = context.Messages, FinalAgent = context.LastNode };
    }

    protected override Task<string> ExecuteAsync(string input, OrchestrationContext context) =>
        ContinueAsync([ModelMessage.User(input)], _start, context);

    private async Task<string> ContinueAsync(List<ModelMessage> conversation, Agent current, OrchestrationContext context)
    {
        for (var handoffs = 0; ; handoffs++)
        {
            string? target = null;
            string? reason = null;
            var transferTools = _agents
                .Where(kv => kv.Value != current && (CanHandoff?.Invoke(current.Name, kv.Value.Name) ?? true))
                .Select(kv => Tool.Create<HandoffInput, string>(
                    kv.Key,
                    string.Format(CultureInfo.CurrentCulture, Strings.Handoff_ToolDescription, kv.Value.Name, kv.Value.Description),
                    input =>
                    {
                        target ??= kv.Key;
                        reason ??= input.Reason;
                        return string.Format(CultureInfo.CurrentCulture, Strings.Handoff_Transferred, kv.Value.Name);
                    }))
                .ToList();

            if (handoffs >= MaxHandoffs)
            {
                transferTools.Clear();
            }

            var options = current.BuildOptions(
                new AgentCallOptions
                {
                    Messages = conversation,
                    Services = context.Options.Services,
                    Context = context.Options.Context,
                    OnToolApproval = context.Options.OnToolApproval,
                    CancellationToken = context.CancellationToken,
                },
                transferTools,
                transferTools.Count == 0 ? null : new StopCondition(steps => steps.Count > 0 && steps[^1].ToolCalls.Any(c => c.ToolName.StartsWith("transfer_to_", StringComparison.Ordinal))));

            await context.EmitAsync(new NodeStartedEvent(current.Name, conversation.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty)).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var result = await Ai.GenerateTextAsync(options).ConfigureAwait(false);
            context.Record(current.Name, conversation.LastOrDefault()?.Text ?? string.Empty, result.Text, result.TotalUsage, watch.Elapsed);
            await context.EmitAsync(new NodeCompletedEvent(current.Name, result.Text, result.TotalUsage, watch.Elapsed)).ConfigureAwait(false);
            conversation.AddRange(result.ResponseMessages);
            context.Messages = conversation;
            context.LastNode = current.Name;

            if (target is null)
            {
                return result.Text;
            }

            var next = _agents[target];
            await context.EmitAsync(new HandoffEvent(current.Name, next.Name, reason)).ConfigureAwait(false);
            current = next;
        }
    }
}

/// <summary>Generates, evaluates and revises until the criteria are met or the iterations run out.</summary>
public sealed class EvaluatorOptimizerOrchestration(IOrchestrationNode generator, ILanguageModel evaluator, string criteria) : Orchestration
{
    public override string Name => "evaluator-optimizer";

    public int MaxIterations { get; init; } = 3;

    /// <summary>Gets or sets the minimum score (0-10) required for approval, in addition to <c>approved</c>.</summary>
    public int? MinScore { get; init; }

    /// <summary>Evaluations from the last run.</summary>
    public sealed record Evaluation(
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Evaluator_Param_Approved))] bool Approved,
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Evaluator_Param_Score))] int Score,
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Evaluator_Param_Feedback))] string Feedback);

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var draft = string.Empty;
        var feedback = string.Empty;
        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var prompt = iteration == 1
                ? input
                : string.Format(CultureInfo.CurrentCulture, Strings.Evaluator_RevisionPrompt, input, draft, feedback);
            draft = await generator.RunAsync(prompt, context).ConfigureAwait(false);
            context.LastNode = generator.Name;

            var watch = Stopwatch.StartNew();
            var evaluation = await Ai.GenerateObjectAsync<Evaluation>(new GenerateObjectOptions
            {
                Model = evaluator,
                System = string.Format(CultureInfo.CurrentCulture, Strings.Evaluator_System, criteria),
                Prompt = string.Format(CultureInfo.CurrentCulture, Strings.Evaluator_Prompt, input, draft),
                CancellationToken = context.CancellationToken,
            }).ConfigureAwait(false);
            var e = evaluation.Object;
            context.Record("evaluator", draft, JsonSerializer.Serialize(e), evaluation.Usage, watch.Elapsed);
            var approved = e.Approved && (MinScore is null || e.Score >= MinScore);
            await context.EmitAsync(new EvaluationEvent(Name, iteration, approved, e.Feedback, e.Score)).ConfigureAwait(false);
            if (approved)
            {
                break;
            }

            feedback = e.Feedback;
        }

        return draft;
    }
}
