using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>Entrada del workflow <see cref="AgentTasks.WorkflowName"/>.</summary>
public sealed record AgentTaskInput(string Task, string Prompt, string? Owner = null);

/// <summary>Resultado de una tarea programada.</summary>
public sealed record AgentTaskResult(string Task, string Prompt, string Text, string? Owner, string RunId);

/// <summary>Opciones de las tareas programadas por agentes.</summary>
public sealed class AgentTaskOptions
{
    /// <summary>Agente que ejecuta cada tarea (recibe los servicios del motor).</summary>
    public required Func<IServiceProvider?, IAgent> Agent { get; init; }

    /// <summary>Se llama al terminar cada tarea: enviar correo, Slack, guardar en BD, notificar en la app...</summary>
    public Func<AgentTaskResult, IServiceProvider?, CancellationToken, Task>? OnCompleted { get; init; }

    /// <summary>Intervalo mínimo permitido entre ejecuciones (evita abusos y costos inesperados).</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Tareas activas máximas por dueño.</summary>
    public int MaxTasksPerOwner { get; init; } = 20;

    /// <summary>
    /// Clave de <see cref="ToolExecutionContext.Context"/> con el usuario dueño. Es obligatoria: sin dueño las herramientas
    /// se niegan a operar (no hay tareas "de todos"). La misma clave llega al agente cuando la tarea se ejecuta.
    /// </summary>
    public string OwnerKey { get; init; } = "userId";

    /// <summary>Zona horaria por defecto para expresiones cron.</summary>
    public string? DefaultTimeZone { get; init; }
}

/// <summary>
/// Tareas programadas por agentes (como "recuérdame cada lunes..." o "revisa los precios todos los días a las 9"):
/// registra el workflow que ejecuta un agente y ofrece herramientas para crear, listar, ejecutar y cancelar
/// automatizaciones desde la conversación.
/// </summary>
public static class AgentTasks
{
    public const string WorkflowName = "agent-task";

    /// <summary>Instrucciones de sistema recomendadas.</summary>
    public const string Instructions =
        """
        Puedes programar tareas que se ejecutarán más tarde sin el usuario presente: schedule_task con una expresión
        cron de 5 campos (minuto hora día mes día-semana), una fecha única (runAt) o un intervalo en minutos.
        El prompt de la tarea debe ser autocontenido: incluye todo el contexto necesario, porque se ejecutará sin esta conversación.
        Confirma con el usuario la frecuencia y la zona horaria antes de programar. Usa list_scheduled_tasks y
        cancel_scheduled_task para gestionarlas.
        """;

    /// <summary>Registra el workflow que ejecuta las tareas.</summary>
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
        }, "Ejecuta un agente con un prompt programado."));

    /// <summary>Herramientas para que el agente gestione tareas programadas.</summary>
    public static IReadOnlyList<Tool> CreateTools(WorkflowEngine engine, AgentTaskOptions options)
    {
        // Sin dueño no se opera: si faltara la clave, todos los usuarios compartirían el dueño null y verían las tareas ajenas.
        string Owner(ToolExecutionContext ctx) =>
            ctx.Context is not null && ctx.Context.TryGetValue(options.OwnerKey, out var value) && value?.ToString() is { } owner && !string.IsNullOrWhiteSpace(owner)
                ? owner
                : throw new UnauthorizedAccessException($"No hay un usuario dueño en el contexto ('{options.OwnerKey}'): las tareas programadas necesitan saber de quién son.");

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
                "Programa una tarea para ejecutarse más tarde (una vez o de forma recurrente). Indica exactamente uno de: cron, runAt o everyMinutes.",
                async (input, ctx, ct) =>
                {
                    var owner = Owner(ctx);
                    var active = (await MineAsync(ctx, ct).ConfigureAwait(false)).Count(a => a.Enabled);
                    if (active >= options.MaxTasksPerOwner)
                    {
                        throw new InvalidOperationException($"Se alcanzó el máximo de {options.MaxTasksPerOwner} tareas activas.");
                    }

                    AutomationTrigger trigger = (input.Cron, input.RunAt, input.EveryMinutes) switch
                    {
                        ({ Length: > 0 } cron, null, null) => new CronTrigger(cron, input.TimeZone ?? options.DefaultTimeZone),
                        (null, { Length: > 0 } runAt, null) => new OnceTrigger(DateTimeOffset.Parse(runAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)),
                        (null, null, { } minutes) => new IntervalTrigger(TimeSpan.FromMinutes(minutes)),
                        _ => throw new ArgumentException("Indica exactamente uno de: cron, runAt o everyMinutes."),
                    };
                    ValidateFrequency(trigger, options.MinimumInterval);
                    if (trigger is OnceTrigger once && once.At <= DateTimeOffset.UtcNow)
                    {
                        throw new ArgumentException("runAt debe ser una fecha futura.");
                    }

                    var automation = Automation.Create(input.Name, trigger, WorkflowName, new AgentTaskInput(input.Name, input.Prompt, owner)) with { Owner = owner };
                    return Describe(await engine.AddAutomationAsync(automation, ct).ConfigureAwait(false));
                }),
            Tool.Create<EmptyInput, object>(
                "list_scheduled_tasks",
                "Lista las tareas programadas del usuario.",
                async (_, ctx, ct) => (await MineAsync(ctx, ct).ConfigureAwait(false)).Select(Describe).ToList()),
            Tool.Create<TaskIdInput, object>(
                "cancel_scheduled_task",
                "Cancela (elimina) una tarea programada.",
                async (input, ctx, ct) =>
                {
                    var automation = (await MineAsync(ctx, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == input.Id)
                                     ?? throw new ArgumentException($"No existe la tarea '{input.Id}'.");
                    await engine.RemoveAutomationAsync(automation.Id, ct).ConfigureAwait(false);
                    return new { id = automation.Id, cancelled = true };
                }),
            Tool.Create<TaskIdInput, object>(
                "run_scheduled_task_now",
                "Ejecuta ahora una tarea programada (sin cambiar su programación).",
                async (input, ctx, ct) =>
                {
                    var automation = (await MineAsync(ctx, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == input.Id)
                                     ?? throw new ArgumentException($"No existe la tarea '{input.Id}'.");
                    var run = await engine.RunAutomationNowAsync(automation.Id, ct).ConfigureAwait(false);
                    return new { runId = run.Id, status = run.Status.ToString() };
                }),
            Tool.Create<TaskIdInput, object>(
                "get_task_result",
                "Devuelve el resultado de la última ejecución de una tarea programada.",
                async (input, ctx, ct) =>
                {
                    var automation = (await MineAsync(ctx, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == input.Id)
                                     ?? throw new ArgumentException($"No existe la tarea '{input.Id}'.");
                    if (automation.LastRunId is null)
                    {
                        return new { status = "nunca ejecutada", nextRunAt = automation.NextRunAt };
                    }

                    var run = await engine.GetRunAsync(automation.LastRunId, ct).ConfigureAwait(false);
                    return new { status = run?.Status.ToString(), ranAt = automation.LastRunAt, result = run?.GetOutput<string>(), error = run?.Error };
                }),
        ];
    }

    /// <summary>
    /// Comprueba que el disparador no se ejecute más seguido que <paramref name="minimum"/>. Para cron se revisa el menor
    /// intervalo entre ocurrencias consecutivas durante 8 días desde la primera (cubre los patrones por hora, día y día de
    /// la semana), no solo entre las dos primeras.
    /// </summary>
    public static void ValidateFrequency(AutomationTrigger trigger, TimeSpan minimum)
    {
        switch (trigger)
        {
            case IntervalTrigger interval when interval.Every < minimum:
                throw new ArgumentException($"El intervalo mínimo es {minimum.TotalMinutes} minutos.");
            case CronTrigger cron:
                trigger.Validate();
                if (cron.NextOccurrence(DateTimeOffset.UtcNow, null) is not { } first)
                {
                    break;
                }

                var horizon = first + TimeSpan.FromDays(8);
                var tooFrequent = new ArgumentException($"La expresión cron se ejecuta más seguido que el mínimo permitido ({minimum.TotalMinutes} minutos).");
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
