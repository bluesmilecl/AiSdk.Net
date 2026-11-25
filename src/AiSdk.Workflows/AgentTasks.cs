using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AiSdk.Workflows.Resources;

namespace AiSdk.Workflows;

/// <summary>Input of the <see cref="AgentTasks.WorkflowName"/> workflow.</summary>
public sealed record AgentTaskInput(string Task, string Prompt, string? Owner = null);

/// <summary>Result of a scheduled task.</summary>
public sealed record AgentTaskResult(string Task, string Prompt, string Text, string? Owner, string RunId);

/// <summary>Options for agent-scheduled tasks.</summary>
public sealed class AgentTaskOptions
{
    /// <summary>Agent that runs each task (receives the engine's services).</summary>
    public required Func<IServiceProvider?, IAgent> Agent { get; init; }

    /// <summary>Called when each task finishes: send an email or a Slack message, save to a database, notify in the app...</summary>
    public Func<AgentTaskResult, IServiceProvider?, CancellationToken, Task>? OnCompleted { get; init; }

    /// <summary>Minimum interval allowed between runs (prevents abuse and unexpected costs).</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Maximum active tasks per owner.</summary>
    public int MaxTasksPerOwner { get; init; } = 20;

    /// <summary>
    /// <see cref="ToolExecutionContext.Context"/> key holding the owning user. It is required: without an owner the tools
    /// refuse to operate (there are no tasks that belong to "everyone"). The same key reaches the agent when the task runs.
    /// </summary>
    public string OwnerKey { get; init; } = "userId";

    /// <summary>Default time zone for cron expressions.</summary>
    public string? DefaultTimeZone { get; init; }
}

/// <summary>
/// Tasks scheduled by agents (such as "remind me every Monday..." or "check prices every day at 9"): registers the
/// workflow that runs an agent and provides tools to create, list, run and cancel automations from the
/// conversation.
/// </summary>
public static class AgentTasks
{
    public const string WorkflowName = "agent-task";

    /// <summary>Recommended system instructions, in the language of <see cref="System.Globalization.CultureInfo.CurrentUICulture"/> (English, or Spanish for "es*").</summary>
    public static string Instructions => Strings.AgentTasks_Instructions;

    /// <summary>Registers the workflow that runs the tasks.</summary>
    public static WorkflowEngine AddAgentTasks(this WorkflowEngine engine, AgentTaskOptions options) =>
        engine.Register(Workflow.Create<AgentTaskInput, string>(WorkflowName, async (ctx, input) =>
        {
            var agent = options.Agent(ctx.Services);
            // El agente se ejecuta en nombre del dueño: sus herramientas lo reciben en el contexto, como en la conversación.
            var context = input.Owner is null ? null : new Dictionary<string, object?> { [options.OwnerKey] = input.Owner };
            var text = await ctx.RunAgentAsync(agent, input.Prompt, "run-agent", context: context).ConfigureAwait(false);
            if (options.OnCompleted is { } onCompleted)
            {
                await ctx.StepAsync("notify", ct => onCompleted(new AgentTaskResult(input.Task, input.Prompt, text, input.Owner, ctx.RunId), ctx.Services, ct)).ConfigureAwait(false);
            }

            return text;
        }, Strings.AgentTasks_WorkflowDescription));

    /// <summary>Tools that let the agent manage scheduled tasks.</summary>
    public static IReadOnlyList<Tool> CreateTools(WorkflowEngine engine, AgentTaskOptions options)
    {
        // Sin dueño no se opera: si faltara la clave, todos los usuarios compartirían el dueño null y verían las tareas ajenas.
        string Owner(ToolExecutionContext ctx) =>
            ctx.Context is not null && ctx.Context.TryGetValue(options.OwnerKey, out var value) && value?.ToString() is { } owner && !string.IsNullOrWhiteSpace(owner)
                ? owner
                : throw new UnauthorizedAccessException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_NoOwner, options.OwnerKey));

        async Task<IReadOnlyList<Automation>> MineAsync(ToolExecutionContext ctx, CancellationToken ct)
        {
            var owner = Owner(ctx);
            return (await engine.ListAutomationsAsync(ct).ConfigureAwait(false))
                .Where(a => a.Workflow == WorkflowName && a.Owner == owner)
                .ToList();
        }

        object Describe(Automation a) => new
        {
            id = a.Id,
            name = a.Name,
            schedule = a.Trigger.Describe(),
            enabled = a.Enabled,
            nextRunAt = a.NextRunAt,
            lastRunAt = a.LastRunAt,
            prompt = a.Input?.Deserialize<AgentTaskInput>(JsonSchema.DefaultSerializerOptions)?.Prompt,
        };

        return
        [
            Tool.Create<ScheduleInput, object>(
                "schedule_task",
                Strings.AgentTasks_ScheduleTaskDescription,
                async (input, ctx, ct) =>
                {
                    var owner = Owner(ctx);
                    var active = (await MineAsync(ctx, ct).ConfigureAwait(false)).Count(a => a.Enabled);
                    if (active >= options.MaxTasksPerOwner)
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_MaxTasksReached, options.MaxTasksPerOwner));
                    }

                    AutomationTrigger trigger = (input.Cron, input.RunAt, input.EveryMinutes) switch
                    {
                        ({ Length: > 0 } cron, null, null) => new CronTrigger(cron, input.TimeZone ?? options.DefaultTimeZone),
                        (null, { Length: > 0 } runAt, null) => new OnceTrigger(DateTimeOffset.Parse(runAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)),
                        (null, null, { } minutes) => new IntervalTrigger(TimeSpan.FromMinutes(minutes)),
                        _ => throw new ArgumentException(Strings.AgentTasks_ExactlyOneSchedule),
                    };
                    ValidateFrequency(trigger, options.MinimumInterval);
                    if (trigger is OnceTrigger once && once.At <= DateTimeOffset.UtcNow)
                    {
                        throw new ArgumentException(Strings.AgentTasks_RunAtNotInFuture);
                    }

                    var automation = Automation.Create(input.Name, trigger, WorkflowName, new AgentTaskInput(input.Name, input.Prompt, owner)) with { Owner = owner };
                    return Describe(await engine.AddAutomationAsync(automation, ct).ConfigureAwait(false));
                }),
            Tool.Create<EmptyInput, object>(
                "list_scheduled_tasks",
                Strings.AgentTasks_ListTasksDescription,
                async (_, ctx, ct) => (await MineAsync(ctx, ct).ConfigureAwait(false)).Select(Describe).ToList()),
            Tool.Create<TaskIdInput, object>(
                "cancel_scheduled_task",
                Strings.AgentTasks_CancelTaskDescription,
                async (input, ctx, ct) =>
                {
                    var automation = (await MineAsync(ctx, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == input.Id)
                                     ?? throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_TaskNotFound, input.Id));
                    await engine.RemoveAutomationAsync(automation.Id, ct).ConfigureAwait(false);
                    return new { id = automation.Id, cancelled = true };
                }),
            Tool.Create<TaskIdInput, object>(
                "run_scheduled_task_now",
                Strings.AgentTasks_RunTaskNowDescription,
                async (input, ctx, ct) =>
                {
                    var automation = (await MineAsync(ctx, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == input.Id)
                                     ?? throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_TaskNotFound, input.Id));
                    var run = await engine.RunAutomationNowAsync(automation.Id, ct).ConfigureAwait(false);
                    return new { runId = run.Id, status = run.Status.ToString() };
                }),
            Tool.Create<TaskIdInput, object>(
                "get_task_result",
                Strings.AgentTasks_GetTaskResultDescription,
                async (input, ctx, ct) =>
                {
                    var automation = (await MineAsync(ctx, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == input.Id)
                                     ?? throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_TaskNotFound, input.Id));
                    if (automation.LastRunId is null)
                    {
                        return new { status = Strings.AgentTasks_NeverRun, nextRunAt = automation.NextRunAt };
                    }

                    var run = await engine.GetRunAsync(automation.LastRunId, ct).ConfigureAwait(false);
                    return new { status = run?.Status.ToString(), ranAt = automation.LastRunAt, result = run?.GetOutput<string>(), error = run?.Error };
                }),
        ];
    }

    /// <summary>
    /// Checks that the trigger does not run more often than <paramref name="minimum"/>. For cron, the shortest interval
    /// between consecutive occurrences is checked over 8 days from the first one (covering hourly, daily and day-of-week
    /// patterns), not just between the first two.
    /// </summary>
    public static void ValidateFrequency(AutomationTrigger trigger, TimeSpan minimum)
    {
        switch (trigger)
        {
            case IntervalTrigger interval when interval.Every < minimum:
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_MinimumInterval, minimum.TotalMinutes));
            case CronTrigger cron:
                trigger.Validate();
                if (cron.NextOccurrence(DateTimeOffset.UtcNow, null) is not { } first)
                {
                    break;
                }

                var horizon = first + TimeSpan.FromDays(8);
                var tooFrequent = new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.AgentTasks_CronTooFrequent, minimum.TotalMinutes));
                // Con ocurrencias separadas al menos por el mínimo, en 8 días caben como mucho 8 días / mínimo.
                var maxOccurrences = minimum > TimeSpan.Zero ? (long)(TimeSpan.FromDays(8) / minimum) + 2 : long.MaxValue;
                var previous = first;
                for (long count = 1; ; count++)
                {
                    if (cron.NextOccurrence(previous, previous) is not { } next || next > horizon)
                    {
                        break;
                    }

                    if (next - previous < minimum || count > maxOccurrences)
                    {
                        throw tooFrequent;
                    }

                    previous = next;
                }

                break;
        }
    }

    private sealed record ScheduleInput(
        [property: Description("Nombre corto de la tarea.")] string Name,
        [property: Description("Instrucciones completas y autocontenidas que ejecutará el agente.")] string Prompt,
        [property: Description("Expresión cron de 5 campos, p. ej. \"0 9 * * 1-5\" (días hábiles a las 9:00).")] string? Cron = null,
        [property: Description("Fecha y hora ISO 8601 para una única ejecución, p. ej. 2026-11-01T09:00:00-03:00.")] string? RunAt = null,
        [property: Description("Repetir cada N minutos.")] int? EveryMinutes = null,
        [property: Description("Zona horaria IANA para cron, p. ej. America/Santiago.")] string? TimeZone = null);

    private sealed record TaskIdInput([property: Description("Id de la tarea.")] string Id);

    private sealed record EmptyInput;
}
