using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiSdk;

/// <summary>
/// Unidad que puede participar en una orquestación: un agente, una función o una orquestación anidada.
/// Los <see cref="Agent"/> ya implementan esta interfaz.
/// </summary>
public interface IOrchestrationNode
{
    string Name { get; }

    /// <summary>Qué hace (lo usan el router y el supervisor para elegir).</summary>
    string? Description { get; }

    Task<string> RunAsync(string input, OrchestrationContext context);
}

/// <summary>Evento emitido durante una orquestación (para registro, métricas o la interfaz).</summary>
public abstract record OrchestrationEvent(string Node)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record NodeStartedEvent(string Node, string Input) : OrchestrationEvent(Node);

public sealed record NodeCompletedEvent(string Node, string Output, Usage Usage, TimeSpan Duration) : OrchestrationEvent(Node);

public sealed record RoutedEvent(string Node, string Route, string? Reason) : OrchestrationEvent(Node);

public sealed record HandoffEvent(string Node, string To, string? Reason) : OrchestrationEvent(Node);

public sealed record EvaluationEvent(string Node, int Iteration, bool Approved, string Feedback, int? Score) : OrchestrationEvent(Node);

/// <summary>Ejecución de un agente dentro de una orquestación.</summary>
public sealed record AgentRun(string Agent, string Input, string Output, Usage Usage, TimeSpan Duration);

/// <summary>Opciones de ejecución de una orquestación.</summary>
public sealed class OrchestrationOptions
{
    public IServiceProvider? Services { get; init; }

    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>Recibe cada evento (inicio/fin de agentes, enrutamientos, traspasos, evaluaciones).</summary>
    public Func<OrchestrationEvent, ValueTask>? OnEvent { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Estado compartido de una ejecución: registra agentes ejecutados, uso de tokens y eventos.</summary>
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

    /// <summary>Último nodo que produjo la respuesta.</summary>
    public string? LastNode { get; set; }

    /// <summary>Conversación final (orquestaciones con traspasos).</summary>
    public IReadOnlyList<ModelMessage>? Messages { get; set; }

    public ValueTask EmitAsync(OrchestrationEvent orchestrationEvent) =>
        Options.OnEvent?.Invoke(orchestrationEvent) ?? ValueTask.CompletedTask;

    /// <summary>Ejecuta un agente registrando su uso y emitiendo eventos.</summary>
    public Task<string> RunAgentAsync(IAgent agent, string input) =>
        RunAgentAsync(agent, new AgentCallOptions { Prompt = input }, input);

    /// <summary>Ejecuta un agente con mensajes y devuelve el resultado completo.</summary>
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
            CancellationToken = CancellationToken,
        }).ConfigureAwait(false);
        Record(agent.Name, inputLabel, result.Text, result.TotalUsage, watch.Elapsed);
        await EmitAsync(new NodeCompletedEvent(agent.Name, result.Text, result.TotalUsage, watch.Elapsed)).ConfigureAwait(false);
        return result;
    }

    private async Task<string> RunAgentAsync(IAgent agent, AgentCallOptions call, string inputLabel) =>
        (await RunAgentResultAsync(agent, call, inputLabel).ConfigureAwait(false)).Text;

    /// <summary>Registra una ejecución hecha fuera de <see cref="RunAgentAsync(IAgent, string)"/> (p. ej. una llamada directa al modelo).</summary>
    public void Record(string agent, string input, string output, Usage usage, TimeSpan duration) =>
        _runs.Enqueue(new AgentRun(agent, input, output, usage, duration));
}

/// <summary>Resultado de una orquestación.</summary>
public sealed record OrchestrationResult(string Text, IReadOnlyList<AgentRun> Runs, Usage TotalUsage)
{
    /// <summary>Conversación final (orquestaciones con traspasos).</summary>
    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    /// <summary>Último agente que respondió (traspasos y router).</summary>
    public string? FinalAgent { get; init; }
}

/// <summary>Base de las orquestaciones. Son nodos, así que se pueden anidar unas dentro de otras.</summary>
public abstract class Orchestration : IOrchestrationNode
{
    public virtual string Name => GetType().Name;

    public virtual string? Description { get; init; }

    /// <summary>Ejecuta la orquestación y devuelve texto, ejecuciones y uso total.</summary>
    public async Task<OrchestrationResult> RunAsync(string input, OrchestrationOptions? options = null)
    {
        var context = new OrchestrationContext(options);
        var text = await RunAsync(input, context).ConfigureAwait(false);
        return new OrchestrationResult(text, context.Runs, context.TotalUsage) { Messages = context.Messages, FinalAgent = context.LastNode };
    }

    public Task<string> RunAsync(string input, OrchestrationContext context) => ExecuteAsync(input, context);

    protected abstract Task<string> ExecuteAsync(string input, OrchestrationContext context);

    /// <summary>Expone la orquestación como herramienta de otro agente.</summary>
    public Tool AsTool(string? name = null, string? description = null) => OrchestrationNodes.AsTool(this, name, description);
}

/// <summary>Utilidades para crear nodos.</summary>
public static class OrchestrationNodes
{
    /// <summary>Nodo a partir de una función (transformaciones, llamadas a APIs, validaciones...).</summary>
    public static IOrchestrationNode Function(string name, Func<string, CancellationToken, Task<string>> run, string? description = null) =>
        new FunctionNode(name, description, run);

    /// <inheritdoc cref="Function(string, Func{string, CancellationToken, Task{string}}, string?)"/>
    public static IOrchestrationNode Function(string name, Func<string, string> run, string? description = null) =>
        new FunctionNode(name, description, (input, _) => Task.FromResult(run(input)));

    /// <summary>Herramienta que ejecuta el nodo con <c>{ "task": "..." }</c>.</summary>
    public static Tool AsTool(IOrchestrationNode node, string? name = null, string? description = null, OrchestrationContext? context = null) =>
        Tool.Create<TaskInput, string>(
            name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(node.Name),
            description ?? node.Description ?? $"Delega una tarea a {node.Name}.",
            (input, ctx, ct) => node.RunAsync(input.Task, context ?? new OrchestrationContext(new OrchestrationOptions
            {
                Services = ctx.Services,
                Context = ctx.Context,
                CancellationToken = ct,
            })));

    /// <summary>Da nombre y descripción a un nodo (p. ej. una orquestación usada como ruta de un router o trabajador de un supervisor).</summary>
    public static IOrchestrationNode Named(this IOrchestrationNode node, string name, string? description = null) =>
        new NamedNode(name, description ?? node.Description, node);

    private sealed class NamedNode(string name, string? description, IOrchestrationNode inner) : IOrchestrationNode
    {
        public string Name => name;

        public string? Description => description;

        public Task<string> RunAsync(string input, OrchestrationContext context) => inner.RunAsync(input, context);
    }

    internal sealed record TaskInput([property: Description("Descripción completa y autocontenida de la tarea.")] string Task);

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
/// Patrones de orquestación multi-agente (ver "Building effective agents" de Anthropic y los
/// patrones de agentes del AI SDK): cadena, paralelo, router, supervisor, traspasos y evaluador-optimizador.
/// </summary>
public static class Orchestrations
{
    /// <summary>Cadena: la salida de cada paso es la entrada del siguiente.</summary>
    public static SequentialOrchestration Sequential(params IOrchestrationNode[] steps) => new(steps);

    /// <summary>Paralelo: todos reciben la misma entrada; los resultados se combinan o los sintetiza un agregador.</summary>
    public static ParallelOrchestration Parallel(IEnumerable<IOrchestrationNode> branches, IOrchestrationNode? aggregator = null) =>
        new(branches.ToList()) { Aggregator = aggregator };

    /// <summary>Router: un modelo clasifica la entrada y la envía al nodo más adecuado.</summary>
    public static RouterOrchestration Router(ILanguageModel classifier, params IOrchestrationNode[] routes) => new(classifier, routes);

    /// <summary>Supervisor (orquestador-trabajadores): un modelo planifica y delega subtareas, en paralelo si conviene.</summary>
    public static SupervisorOrchestration Supervisor(ILanguageModel model, params IOrchestrationNode[] workers) => new(model, workers);

    /// <summary>Traspasos: los agentes se ceden la conversación entre sí (estilo Swarm / OpenAI Agents).</summary>
    public static HandoffOrchestration Handoffs(Agent start, params Agent[] others) => new(start, others);

    /// <summary>Evaluador-optimizador: genera, evalúa contra criterios y corrige hasta aprobar.</summary>
    public static EvaluatorOptimizerOrchestration EvaluatorOptimizer(IOrchestrationNode generator, ILanguageModel evaluator, string criteria) =>
        new(generator, evaluator, criteria);
}

/// <summary>Cadena de pasos.</summary>
public sealed class SequentialOrchestration(IReadOnlyList<IOrchestrationNode> steps) : Orchestration
{
    public override string Name => "sequential";

    /// <summary>Si es true, cada paso recibe también la entrada original además de la salida anterior.</summary>
    public bool IncludeOriginalInput { get; init; }

    /// <summary>Detiene la cadena si una salida cumple la condición (p. ej. una validación fallida).</summary>
    public Func<string, string, bool>? StopWhen { get; init; }

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var current = input;
        foreach (var step in steps)
        {
            var stepInput = IncludeOriginalInput && !ReferenceEquals(current, input)
                ? $"Tarea original:\n{input}\n\nResultado del paso anterior:\n{current}"
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

/// <summary>Ramas en paralelo con agregación.</summary>
public sealed class ParallelOrchestration(IReadOnlyList<IOrchestrationNode> branches) : Orchestration
{
    public override string Name => "parallel";

    /// <summary>Nodo que sintetiza los resultados (recibe la tarea original y todas las respuestas).</summary>
    public IOrchestrationNode? Aggregator { get; init; }

    /// <summary>Combinación sin modelo (si no hay agregador). Por defecto, una sección por rama.</summary>
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
        return await Aggregator.RunAsync($"Tarea original:\n{input}\n\nRespuestas de los especialistas:\n\n{combined}", context).ConfigureAwait(false);
    }
}

/// <summary>Clasifica y enruta al nodo adecuado.</summary>
public sealed class RouterOrchestration(ILanguageModel classifier, IReadOnlyList<IOrchestrationNode> routes) : Orchestration
{
    public override string Name => "router";

    /// <summary>Instrucciones adicionales para el clasificador.</summary>
    public string? Instructions { get; init; }

    /// <summary>Nodo usado si el clasificador no elige ninguna ruta válida.</summary>
    public IOrchestrationNode? Fallback { get; init; }

    private sealed record Decision(
        [property: Description("Nombre exacto de la ruta elegida.")] string Route,
        [property: Description("Motivo breve.")] string Reason);

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var catalog = string.Join("\n", routes.Select(r => $"- {r.Name}: {r.Description ?? "(sin descripción)"}"));
        var watch = Stopwatch.StartNew();
        var decision = await Ai.GenerateObjectAsync<Decision>(new GenerateObjectOptions
        {
            Model = classifier,
            System = $"Elige la ruta más adecuada para la solicitud del usuario.\nRutas disponibles:\n{catalog}\n{Instructions}",
            Prompt = input,
            Temperature = 0,
            CancellationToken = context.CancellationToken,
        }).ConfigureAwait(false);
        context.Record(Name, input, decision.Object.Route, decision.Usage, watch.Elapsed);

        var route = routes.FirstOrDefault(r => string.Equals(r.Name, decision.Object.Route.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? Fallback
                    ?? throw new AiSdkException($"El router eligió una ruta inexistente: '{decision.Object.Route}'.");
        await context.EmitAsync(new RoutedEvent(Name, route.Name, decision.Object.Reason)).ConfigureAwait(false);
        context.LastNode = route.Name;
        return await route.RunAsync(input, context).ConfigureAwait(false);
    }
}

/// <summary>
/// Un modelo supervisor descompone la tarea y delega en trabajadores expuestos como herramientas.
/// Si el modelo pide varias delegaciones en el mismo paso, se ejecutan en paralelo.
/// </summary>
public sealed class SupervisorOrchestration(ILanguageModel model, IReadOnlyList<IOrchestrationNode> workers) : Orchestration
{
    public const string DefaultInstructions =
        "Eres un supervisor que coordina a un equipo de especialistas. Descompón la tarea, delega cada subtarea " +
        "al especialista adecuado con instrucciones completas y autocontenidas (no ven la conversación), " +
        "delega en paralelo cuando las subtareas sean independientes, revisa los resultados y entrega la respuesta final integrada.";

    public override string Name => "supervisor";

    public string Instructions { get; init; } = DefaultInstructions;

    /// <summary>Herramientas propias del supervisor además de los trabajadores.</summary>
    public IReadOnlyList<Tool> Tools { get; init; } = [];

    public int MaxSteps { get; init; } = 15;

    public ProviderOptions? ProviderOptions { get; init; }

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var supervisor = new ToolLoopAgent(new AgentSettings
        {
            Name = Name,
            Model = model,
            Instructions = $"{Instructions}\n\nEspecialistas:\n{string.Join("\n", workers.Select(w => $"- {w.Name}: {w.Description}"))}",
            Tools = [.. workers.Select(w => OrchestrationNodes.AsTool(w, context: context)), .. Tools],
            StopWhen = StopCondition.StepCountIs(MaxSteps),
            ProviderOptions = ProviderOptions,
        });
        context.LastNode = Name;
        return await context.RunAgentAsync(supervisor, input).ConfigureAwait(false);
    }
}

/// <summary>
/// Agentes que se traspasan la conversación. Cada agente recibe herramientas <c>transfer_to_*</c>
/// hacia los demás; al usarlas, el siguiente agente continúa con todo el historial.
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

    /// <summary>Máximo de traspasos antes de detenerse (evita ciclos).</summary>
    public int MaxHandoffs { get; init; } = 8;

    /// <summary>Quién puede traspasar a quién. Por defecto, todos a todos.</summary>
    public Func<string, string, bool>? CanHandoff { get; init; }

    private static string ToolName(string agentName) => "transfer_to_" + JsonNamingPolicy.SnakeCaseLower.ConvertName(agentName);

    private sealed record HandoffInput([property: Description("Motivo del traspaso y contexto relevante para el siguiente agente.")] string Reason);

    /// <summary>Continúa una conversación existente (p. ej. un chat) desde el agente indicado.</summary>
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
                    $"Transfiere la conversación al agente {kv.Value.Name}. {kv.Value.Description}",
                    input =>
                    {
                        target ??= kv.Key;
                        reason ??= input.Reason;
                        return $"Conversación transferida a {kv.Value.Name}.";
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

/// <summary>Genera, evalúa y corrige hasta cumplir los criterios o agotar las iteraciones.</summary>
public sealed class EvaluatorOptimizerOrchestration(IOrchestrationNode generator, ILanguageModel evaluator, string criteria) : Orchestration
{
    public override string Name => "evaluator-optimizer";

    public int MaxIterations { get; init; } = 3;

    /// <summary>Puntaje mínimo (0-10) para aprobar, además de <c>approved</c>.</summary>
    public int? MinScore { get; init; }

    /// <summary>Evaluaciones de la última ejecución.</summary>
    public sealed record Evaluation(
        [property: Description("True si el resultado cumple todos los criterios.")] bool Approved,
        [property: Description("Puntaje de 0 a 10.")] int Score,
        [property: Description("Correcciones concretas y accionables; vacío si está aprobado.")] string Feedback);

    protected override async Task<string> ExecuteAsync(string input, OrchestrationContext context)
    {
        var draft = string.Empty;
        var feedback = string.Empty;
        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var prompt = iteration == 1
                ? input
                : $"{input}\n\nTu versión anterior:\n{draft}\n\nCorrige según esta evaluación:\n{feedback}";
            draft = await generator.RunAsync(prompt, context).ConfigureAwait(false);
            context.LastNode = generator.Name;

            var watch = Stopwatch.StartNew();
            var evaluation = await Ai.GenerateObjectAsync<Evaluation>(new GenerateObjectOptions
            {
                Model = evaluator,
                System = $"Eres un evaluador exigente. Evalúa el resultado contra estos criterios:\n{criteria}",
                Prompt = $"Tarea:\n{input}\n\nResultado a evaluar:\n{draft}",
                Temperature = 0,
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
