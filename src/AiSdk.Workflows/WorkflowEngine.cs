using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>Opciones del motor.</summary>
public sealed class WorkflowEngineOptions
{
    /// <summary>Ejecuciones simultáneas máximas.</summary>
    public int MaxConcurrency { get; init; } = 16;

    /// <summary>Recibe cada cambio de estado de una ejecución (registro, notificaciones, UI).</summary>
    public Func<WorkflowRun, Task>? OnRunChanged { get; init; }
}

/// <summary>
/// Motor de workflows durables y automatizaciones. Ejecuta, suspende y reanuda workflows; atiende
/// eventos y webhooks; y dispara automatizaciones programadas en <see cref="TickAsync"/> (que llama
/// periódicamente el servicio en segundo plano de <c>AddAiSdkWorkflows</c>).
/// </summary>
public sealed class WorkflowEngine
{
    private readonly ConcurrentDictionary<string, IWorkflow> _workflows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runLocks = new();
    private readonly ConcurrentDictionary<string, Task> _background = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly WorkflowEngineOptions _options;

    public WorkflowEngine(IWorkflowStore? store = null, IServiceProvider? services = null, WorkflowEngineOptions? options = null)
    {
        Store = store ?? new InMemoryWorkflowStore();
        Services = services;
        _options = options ?? new WorkflowEngineOptions();
        _concurrency = new SemaphoreSlim(_options.MaxConcurrency);
    }

    public IWorkflowStore Store { get; }

    public IServiceProvider? Services { get; }

    public IReadOnlyCollection<IWorkflow> Workflows => _workflows.Values.ToList();

    public WorkflowEngine Register(IWorkflow workflow)
    {
        _workflows[workflow.Name] = workflow;
        return this;
    }

    public WorkflowEngine Register(params IWorkflow[] workflows)
    {
        foreach (var workflow in workflows) Register(workflow);
        return this;
    }

    // ---------------------------------------------------------------------
    // Ejecuciones
    // ---------------------------------------------------------------------

    /// <summary>
    /// Inicia una ejecución. Con <paramref name="wait"/> = true espera hasta que termine o se suspenda;
    /// con false la ejecuta en segundo plano y devuelve de inmediato.
    /// <paramref name="runId"/> sirve como clave de idempotencia: si ya existe, se devuelve esa ejecución.
    /// </summary>
    public async Task<WorkflowRun> StartAsync<TInput>(string workflowName, TInput input, string? runId = null, string? triggeredBy = null, bool wait = true, CancellationToken cancellationToken = default)
    {
        if (!_workflows.ContainsKey(workflowName))
        {
            throw new ArgumentException($"El workflow '{workflowName}' no está registrado.", nameof(workflowName));
        }

        if (runId is not null && await Store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var run = new WorkflowRun
        {
            Id = runId ?? $"run_{Guid.NewGuid():N}",
            WorkflowName = workflowName,
            Input = input is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(input, JsonSchema.DefaultSerializerOptions),
            TriggeredBy = triggeredBy ?? "manual",
        };
        await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return wait ? await ExecuteAsync(run.Id, cancellationToken).ConfigureAwait(false) : Background(run);
    }

    /// <summary>Reanuda una ejecución suspendida o interrumpida.</summary>
    public Task<WorkflowRun> ResumeAsync(string runId, CancellationToken cancellationToken = default) => ExecuteAsync(runId, cancellationToken);

    /// <summary>
    /// Reintenta una ejecución fallida: los pasos completados se conservan y continúa desde el que falló.
    /// </summary>
    public async Task<WorkflowRun> RetryAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.Status != WorkflowStatus.Failed)
        {
            return run;
        }

        run.Status = WorkflowStatus.Pending;
        run.Error = null;
        await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowRun> CancelAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
        if (!run.IsFinished)
        {
            run.Status = WorkflowStatus.Cancelled;
            run.Waiting = null;
            run.ResumeAt = null;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    public Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default) => Store.GetRunAsync(runId, cancellationToken);

    /// <summary>Espera a que terminen las ejecuciones lanzadas en segundo plano (útil en pruebas y al apagar).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_background.Values);

    /// <summary>
    /// Envía un evento: reanuda las ejecuciones que lo esperan y dispara las automatizaciones con
    /// <see cref="EventTrigger"/> que coincidan. Devuelve los ids de las ejecuciones afectadas.
    /// </summary>
    public async Task<IReadOnlyList<string>> SendEventAsync(string eventName, object? payload = null, string? key = null, CancellationToken cancellationToken = default)
    {
        var element = payload is JsonElement e ? e.Clone() : JsonSerializer.SerializeToElement(payload, JsonSchema.DefaultSerializerOptions);
        var affected = new List<string>();

        foreach (var waiting in await Store.ListRunsAsync(WorkflowStatus.WaitingForEvent, limit: int.MaxValue, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (waiting.Waiting is not { } wait || wait.EventName != eventName || (wait.Key is not null && wait.Key != key))
            {
                continue;
            }

            var gate = _runLocks.GetOrAdd(waiting.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            WorkflowRun run;
            try
            {
                run = await GetRequiredAsync(waiting.Id, cancellationToken).ConfigureAwait(false);
                if (run.Waiting?.StepKey != wait.StepKey)
                {
                    continue;
                }

                run.Steps[wait.StepKey] = new StepRecord(wait.StepKey, element, 1, DateTimeOffset.UtcNow);
                run.Waiting = null;
                run.ResumeAt = null;
                run.Status = WorkflowStatus.Pending;
                run.Logs.Add($"{DateTimeOffset.UtcNow:O} Evento recibido: {eventName}{(key is null ? string.Empty : $" ({key})")}");
                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            Background(run);
            affected.Add(run.Id);
        }

        foreach (var automation in await Store.ListAutomationsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (automation.Enabled && automation.Trigger is EventTrigger trigger && trigger.Matches(eventName, element))
            {
                var run = await FireAsync(automation, element, $"event:{eventName}", cancellationToken).ConfigureAwait(false);
                affected.Add(run.Id);
            }
        }

        return affected;
    }

    private async Task<WorkflowRun> GetRequiredAsync(string runId, CancellationToken cancellationToken) =>
        await Store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la ejecución '{runId}'.");

    /// <summary>Ejecuta en segundo plano; no duplica si ya hay una ejecución en cola para el mismo estado guardado.</summary>
    private WorkflowRun Background(WorkflowRun run)
    {
        var key = $"{run.Id}@{run.UpdatedAt.UtcTicks}";
        var start = new TaskCompletionSource();
        var task = start.Task.ContinueWith(
            async _ =>
            {
                try
                {
                    await ExecuteAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _background.TryRemove(key, out var _);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
        if (_background.TryAdd(key, task))
        {
            start.SetResult();
        }

        return run;
    }

    private async Task<WorkflowRun> ExecuteAsync(string runId, CancellationToken cancellationToken)
    {
        var gate = _runLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run.IsFinished)
            {
                return run;
            }

            if (!_workflows.TryGetValue(run.WorkflowName, out var workflow))
            {
                run.Status = WorkflowStatus.Failed;
                run.Error = $"El workflow '{run.WorkflowName}' no está registrado.";
                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
                return run;
            }

            run.Status = WorkflowStatus.Running;
            run.Executions++;
            run.ResumeAt = null;
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);

            var context = new WorkflowContext(run, this, Services, cancellationToken);
            try
            {
                var output = await workflow.ExecuteAsync(context, run.Input).ConfigureAwait(false);
                run.Output = JsonSerializer.SerializeToElement(output, JsonSchema.DefaultSerializerOptions);
                run.Status = WorkflowStatus.Completed;
                run.CompletedAt = DateTimeOffset.UtcNow;
            }
            catch (WorkflowSuspendedException suspended)
            {
                run.Status = suspended.Status;
                run.ResumeAt = suspended.ResumeAt;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Apagado del proceso: queda pendiente y se reanuda al volver.
                run.Status = WorkflowStatus.Pending;
            }
            catch (Exception ex)
            {
                run.Status = WorkflowStatus.Failed;
                run.Error = ex.Message;
                run.Logs.Add($"{DateTimeOffset.UtcNow:O} Error: {ex}");
                run.CompletedAt = DateTimeOffset.UtcNow;
            }

            await SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
            return run;
        }
        finally
        {
            _concurrency.Release();
            gate.Release();
        }
    }

    private async Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await Store.SaveRunAsync(run, cancellationToken).ConfigureAwait(false);
        if (_options.OnRunChanged is { } callback)
        {
            await callback(run).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------
    // Automatizaciones
    // ---------------------------------------------------------------------

    /// <summary>Crea o actualiza una automatización (calcula su próxima ejecución).</summary>
    public async Task<Automation> AddAutomationAsync(Automation automation, CancellationToken cancellationToken = default)
    {
        if (!_workflows.ContainsKey(automation.Workflow))
        {
            throw new ArgumentException($"El workflow '{automation.Workflow}' no está registrado.");
        }

        if (automation.Trigger is CronTrigger cron)
        {
            CronTrigger.Parse(cron.Expression); // valida
        }

        var saved = automation with { NextRunAt = automation.Trigger.NextOccurrence(DateTimeOffset.UtcNow, automation.LastRunAt) };
        await Store.SaveAutomationAsync(saved, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default) => Store.ListAutomationsAsync(cancellationToken);

    public Task RemoveAutomationAsync(string id, CancellationToken cancellationToken = default) => Store.DeleteAutomationAsync(id, cancellationToken);

    public async Task<Automation?> SetAutomationEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        if (await Store.GetAutomationAsync(id, cancellationToken).ConfigureAwait(false) is not { } automation)
        {
            return null;
        }

        return await AddAutomationAsync(automation with { Enabled = enabled }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ejecuta una automatización ahora (manualmente), en segundo plano.</summary>
    public async Task<WorkflowRun> RunAutomationNowAsync(string id, CancellationToken cancellationToken = default)
    {
        var automation = await Store.GetAutomationAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la automatización '{id}'.");
        return await FireAsync(automation, null, $"manual:{id}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atiende un webhook. Verifica el secreto (cabecera <c>X-Webhook-Secret</c>) o la firma HMAC-SHA256
    /// (<c>X-Signature-256: sha256=...</c>) y dispara el workflow con el cuerpo como entrada.
    /// </summary>
    public async Task<WorkflowRun> TriggerWebhookAsync(string automationId, ReadOnlyMemory<byte> body, string? secretHeader = null, string? signatureHeader = null, CancellationToken cancellationToken = default)
    {
        var automation = await Store.GetAutomationAsync(automationId, cancellationToken).ConfigureAwait(false);
        if (automation is not { Enabled: true, Trigger: WebhookTrigger trigger })
        {
            throw new KeyNotFoundException($"No existe un webhook activo '{automationId}'.");
        }

        if (trigger.Secret is { } secret && !VerifyWebhook(secret, body.Span, secretHeader, signatureHeader))
        {
            throw new UnauthorizedAccessException("Firma o secreto del webhook inválido.");
        }

        JsonElement payload;
        try
        {
            payload = body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            payload = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(body.Span));
        }

        return await FireAsync(automation, payload, $"webhook:{automationId}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Comprueba el secreto o la firma HMAC-SHA256 de un webhook en tiempo constante.</summary>
    public static bool VerifyWebhook(string secret, ReadOnlySpan<byte> body, string? secretHeader, string? signatureHeader)
    {
        if (secretHeader is not null)
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secretHeader), Encoding.UTF8.GetBytes(secret));
        }

        if (signatureHeader is null)
        {
            return false;
        }

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signatureHeader.Trim().ToLowerInvariant()), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// Procesa lo pendiente: dispara automatizaciones programadas vencidas, reanuda ejecuciones dormidas o
    /// con esperas vencidas. Lo llama periódicamente el servicio en segundo plano; también puede llamarse a mano.
    /// </summary>
    public async Task TickAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var current = now ?? DateTimeOffset.UtcNow;

        foreach (var automation in await Store.ListAutomationsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (automation.Enabled && automation.NextRunAt is { } due && due <= current)
            {
                await FireAsync(automation, null, $"schedule:{automation.Id}", cancellationToken, current).ConfigureAwait(false);
            }
        }

        foreach (var status in new[] { WorkflowStatus.Sleeping, WorkflowStatus.WaitingForEvent })
        {
            foreach (var run in await Store.ListRunsAsync(status, limit: int.MaxValue, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (run.ResumeAt is { } resumeAt && resumeAt <= current)
                {
                    Background(run);
                }
            }
        }
    }

    /// <summary>Reanuda ejecuciones que quedaron a medias por un reinicio (estado Running o Pending).</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var status in new[] { WorkflowStatus.Running, WorkflowStatus.Pending })
        {
            foreach (var run in await Store.ListRunsAsync(status, limit: int.MaxValue, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                Background(run);
            }
        }
    }

    private async Task<WorkflowRun> FireAsync(Automation automation, JsonElement? payload, string triggeredBy, CancellationToken cancellationToken, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var input = automation.Input ?? (payload is { ValueKind: not JsonValueKind.Undefined } p ? p : JsonSerializer.SerializeToElement(new { }));
        var run = await StartAsync(automation.Workflow, input, triggeredBy: triggeredBy, wait: false, cancellationToken: cancellationToken).ConfigureAwait(false);

        var next = automation.Trigger.NextOccurrence(current, current);
        await Store.SaveAutomationAsync(automation with
        {
            LastRunAt = current,
            LastRunId = run.Id,
            NextRunAt = next,
            Enabled = automation.Enabled && !(automation.Trigger is OnceTrigger),
        }, cancellationToken).ConfigureAwait(false);
        return run;
    }
}
