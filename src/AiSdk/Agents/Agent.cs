using System.ComponentModel;
using System.Text.Json;

namespace AiSdk;

/// <summary>Contrato común de los agentes.</summary>
public interface IAgent
{
    string Name { get; }

    Task<GenerateTextResult> GenerateAsync(AgentCallOptions options);

    StreamTextResult Stream(AgentCallOptions options);
}

/// <summary>Entrada de una llamada a un agente.</summary>
public sealed class AgentCallOptions
{
    public string? Prompt { get; init; }

    public IReadOnlyList<ModelMessage>? Messages { get; init; }

    /// <summary>Servicios accesibles desde las herramientas.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Estado accesible desde las herramientas (p. ej. id del usuario autenticado).</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>Callback adicional por paso (se ejecuta después del del agente).</summary>
    public Func<StepResult, ValueTask>? OnStepFinish { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public static implicit operator AgentCallOptions(string prompt) => new() { Prompt = prompt };
}

/// <summary>
/// Clase base para agentes definidos como clases (estilo Laravel AI SDK):
/// sobrescribe <see cref="Model"/>, <see cref="Instructions"/>, <see cref="Tools"/>, etc.
/// Encapsula el bucle de herramientas de <see cref="Ai.GenerateTextAsync(GenerateTextOptions)"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class SupportAgent(ILanguageModel model) : Agent
/// {
///     protected override ILanguageModel Model => model;
///     protected override string Instructions => "Eres un agente de soporte.";
///     protected override IEnumerable&lt;Tool&gt; Tools => [Tool.FromDelegate("lookup_order", "Busca un pedido", LookupOrder)];
/// }
/// </code>
/// </example>
public abstract class Agent : IAgent
{
    /// <summary>Nombre del agente (por defecto, el nombre de la clase).</summary>
    public virtual string Name => GetType().Name;

    /// <summary>Descripción usada cuando el agente se expone como herramienta.</summary>
    public virtual string? Description => null;

    protected abstract ILanguageModel Model { get; }

    protected virtual string? Instructions => null;

    protected virtual IEnumerable<Tool> Tools => [];

    protected virtual ToolChoice? ToolChoice => null;

    /// <summary>Por defecto el agente se detiene tras 20 pasos.</summary>
    protected virtual StopCondition StopWhen => StopCondition.StepCountIs(20);

    protected virtual Output? Output => null;

    protected virtual CallSettings Settings => new();

    protected virtual ValueTask<PrepareStepResult?> PrepareStepAsync(PrepareStepContext context) => ValueTask.FromResult<PrepareStepResult?>(null);

    protected virtual ValueTask OnStepFinishAsync(StepResult step) => ValueTask.CompletedTask;

    protected virtual ValueTask<bool> ApproveToolCallAsync(ToolApprovalRequest request) => ValueTask.FromResult(false);

    /// <summary>Indica si el agente gestiona aprobaciones (si no, quedan pendientes en el resultado).</summary>
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

    /// <summary>Crea una sesión con memoria de conversación.</summary>
    public AgentSession CreateSession(string? conversationId = null, IConversationStore? store = null) =>
        new(this, conversationId ?? Guid.NewGuid().ToString("N"), store ?? new InMemoryConversationStore());

    /// <summary>
    /// Expone el agente como herramienta para otro agente (sub-agentes / multi-agente).
    /// La herramienta recibe <c>{ "task": "..." }</c> y devuelve el texto final del sub-agente.
    /// </summary>
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
                    CancellationToken = ct,
                }).ConfigureAwait(false);
                return result.Text;
            });

    internal GenerateTextOptions BuildOptions(AgentCallOptions call)
    {
        var s = Settings;
        return new GenerateTextOptions
        {
            Model = Model,
            System = Instructions,
            Prompt = call.Prompt,
            Messages = call.Messages,
            Tools = Tools.ToList(),
            ToolChoice = ToolChoice,
            StopWhen = StopWhen,
            Output = Output,
            PrepareStep = PrepareStepAsync,
            OnStepFinish = call.OnStepFinish is null
                ? OnStepFinishAsync
                : async step =>
                {
                    await OnStepFinishAsync(step).ConfigureAwait(false);
                    await call.OnStepFinish(step).ConfigureAwait(false);
                },
            OnToolApproval = HandlesApprovals ? ApproveToolCallAsync : s.OnToolApproval,
            MaxOutputTokens = s.MaxOutputTokens,
            Temperature = s.Temperature,
            TopP = s.TopP,
            TopK = s.TopK,
            PresencePenalty = s.PresencePenalty,
            FrequencyPenalty = s.FrequencyPenalty,
            StopSequences = s.StopSequences,
            Seed = s.Seed,
            MaxRetries = s.MaxRetries,
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

/// <summary>Configuración de <see cref="ToolLoopAgent"/>.</summary>
public sealed class AgentSettings : CallSettings
{
    public required ILanguageModel Model { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<Tool>? Tools { get; init; }

    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Por defecto <c>StepCountIs(20)</c>.</summary>
    public StopCondition? StopWhen { get; init; }

    public Output? Output { get; init; }

    public Func<PrepareStepContext, ValueTask<PrepareStepResult?>>? PrepareStep { get; init; }

    public Func<StepResult, ValueTask>? OnStepFinish { get; init; }
}

/// <summary>
/// Agente configurable por objeto (equivalente a <c>new ToolLoopAgent({...})</c> del AI SDK):
/// llama al modelo, ejecuta herramientas y repite hasta cumplir la condición de parada.
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

    protected override CallSettings Settings => AgentSettings;

    protected override ValueTask<PrepareStepResult?> PrepareStepAsync(PrepareStepContext context) =>
        AgentSettings.PrepareStep?.Invoke(context) ?? base.PrepareStepAsync(context);

    protected override ValueTask OnStepFinishAsync(StepResult step) =>
        AgentSettings.OnStepFinish?.Invoke(step) ?? ValueTask.CompletedTask;
}
