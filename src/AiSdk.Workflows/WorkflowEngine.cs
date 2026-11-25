using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>Engine options.</summary>
public sealed class WorkflowEngineOptions
{
    /// <summary>Maximum concurrent runs.</summary>
    public int MaxConcurrency { get; init; } = 16;

    /// <summary>Receives every status change of a run (logging, notifications, UI).</summary>
    public Func<WorkflowRun, Task>? OnRunChanged { get; init; }

    /// <summary>Receives the errors the engine isolates in order to keep working (failing automations, background runs...).</summary>
    public Action<Exception, string>? OnError { get; init; }

    /// <summary>Engine clock (useful in tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Identifier of this engine instance, owner of the claims (<see cref="WorkflowRun.LeaseOwner"/>). By default, a
    /// different one in each process. Must be unique among the replicas that share the store.
    /// </summary>
    public string InstanceId { get; init; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>
    /// Duration of the claim on a run. While the run executes, the claim is renewed every quarter of this time; if the
    /// instance dies, another one takes it over once it expires. Defaults to 2 minutes.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum difference between the timestamp of a signed webhook and the engine clock. Defaults to 5 minutes.</summary>
    public TimeSpan WebhookTolerance { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Webhook delivery ids remembered to deduplicate retries. Defaults to 10,000.</summary>
    public int WebhookDeliveryCacheSize { get; init; } = 10_000;

    /// <summary>Consecutive failures after which an automation is disabled. Defaults to 5.</summary>
    public int MaxAutomationFailures { get; init; } = 5;

    /// <summary>Delay after an automation's first failure; it doubles with each consecutive failure. Defaults to 1 minute.</summary>
    public TimeSpan AutomationRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Maximum delay between retries of a failing automation. Defaults to 1 hour.</summary>
    public TimeSpan MaxAutomationRetryDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Maximum automations and runs handled in each query of the cycle. Defaults to 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>
    /// How long finished runs are kept: <see cref="WorkflowEngine.TickAsync"/> deletes (at most once per hour) those that
    /// finished earlier. null = keep them forever. Defaults to 30 days.
    /// </summary>
    public TimeSpan? RunRetention { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Engine for durable workflows and automations. Runs, suspends and resumes workflows; handles events and webhooks;
/// and fires scheduled automations in <see cref="TickAsync"/> (called periodically by the background service of
/// <c>AddAiSdkWorkflows</c>).
/// </summary>
/// <remarks>
/// Several replicas can share an <see cref="IWorkflowStore"/>: saves use optimistic concurrency
/// (<see cref="WorkflowRun.Version"/>), each run is claimed with a lease that is renewed while it progresses, and each
/// scheduled occurrence creates a run with a deterministic id, so the same occurrence is never executed twice.
/// </remarks>
public sealed class WorkflowEngine : IDisposable
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, IWorkflow> _workflows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunGate> _runLocks = new(StringComparer.Ordinal);
    private readonly Lock _runLocksGate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _executing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _background = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly WorkflowEngineOptions _options;
    private readonly WebhookDeliveryCache _deliveries;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public WorkflowEngine(IWorkflowStore? store = null, IServiceProvider? services = null, WorkflowEngineOptions? options = null)
    {
        Store = store ?? new InMemoryWorkflowStore();
        Services = services;
        _options = options ?? new WorkflowEngineOptions();
        if (_options.LeaseDuration < TimeSpan.FromSeconds(1) || _options.BatchSize <= 0 || _options.MaxAutomationFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LeaseDuration debe ser de al menos 1 s y BatchSize y MaxAutomationFailures mayores que cero.");
        }

        _concurrency = new SemaphoreSlim(_options.MaxConcurrency);
        _deliveries = new WebhookDeliveryCache(Math.Max(1, _options.WebhookDeliveryCacheSize));
    }

    public IWorkflowStore Store { get; }

    /// <summary>Identifier of this instance (<see cref="WorkflowEngineOptions.InstanceId"/>).</summary>
    public string InstanceId => _options.InstanceId;

    internal TimeProvider TimeProvider => _options.TimeProvider;

    internal DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    /// <summary>Live per-run semaphores (released when each run finishes).</summary>
    internal int RunLockCount
    {
        get
        {
            lock (_runLocksGate)
            {
                return _runLocks.Count;
            }
        }
    }

    /// <summary>Releases the internal semaphores (the DI container calls it when the application shuts down).</summary>
    public void Dispose()
    {
        _concurrency.Dispose();
        lock (_runLocksGate)
        {
            foreach (var gate in _runLocks.Values)
            {
                gate.Dispose();
            }

            _runLocks.Clear();
        }
    }

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
    /// Starts a run. With <paramref name="wait"/> = true, waits until it finishes or is suspended; with false, runs it in
    /// the background and returns immediately.
    /// <paramref name="runId"/> serves as an idempotency key: if it already exists, that run is returned (also if another
    /// replica creates it at the same time).
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

        var now = Now;
        var run = new WorkflowRun
        {
            Id = runId ?? $"run_{Guid.NewGuid():N}",
            WorkflowName = workflowName,
            Input = input is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(input, JsonSchema.DefaultSerializerOptions),
            TriggeredBy = triggeredBy ?? "manual",
            CreatedAt = now,
            // Reclamada por esta instancia hasta que la ejecute; si el proceso muere, otra la retoma al vencer.
            LeaseOwner = InstanceId,
            LeaseExpiresAt = now + _options.LeaseDuration,
        };

        try
        {
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowConcurrencyException) when (runId is not null)
        {
            // Otra réplica creó la misma ejecución a la vez: se devuelve la suya.
            return await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return wait ? await ExecuteAsync(run.Id, run.Version, cancellationToken).ConfigureAwait(false) : Background(run);
    }

    /// <summary>Resumes a suspended or interrupted run (if no other instance holds a claim on it).</summary>
    public Task<WorkflowRun> ResumeAsync(string runId, CancellationToken cancellationToken = default) => ExecuteAsync(runId, null, cancellationToken);

    /// <summary>
    /// Retries a failed run: completed steps are kept and it continues from the one that failed.
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
        run.CompletedAt = null;
        Lease(run);
        try
        {
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowConcurrencyException)
        {
            return await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteAsync(runId, run.Version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a run. The <see cref="WorkflowStatus.Cancelled"/> status is final: if the run is in progress (on this or
    /// another replica), it is notified through its <see cref="WorkflowContext.CancellationToken"/> and its subsequent
    /// saves are rejected, so it cannot overwrite that status.
    /// </summary>
    public async Task<WorkflowRun> CancelAsync(string runId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var run = await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run.IsFinished)
            {
                return run;
            }

            run.Status = WorkflowStatus.Cancelled;
            run.Waiting = null;
            run.ResumeAt = null;
            run.CompletedAt = Now;
            run.LeaseOwner = null;
            run.LeaseExpiresAt = null;
            run.Logs.Add($"{Now:O} Ejecución cancelada.");
            try
            {
                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkflowConcurrencyException) when (attempt < 20)
            {
                continue; // La ejecución guardó un paso entretanto: se vuelve a leer.
            }

            // Guardado el estado final, se avisa a la ejecución en curso en este proceso para que se detenga ya.
            if (_executing.TryGetValue(runId, out var execution))
            {
                try
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return run;
        }
    }

    public Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken cancellationToken = default) => Store.GetRunAsync(runId, cancellationToken);

    /// <summary>Waits for the runs launched in the background to finish (useful in tests and at shutdown).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_background.Values);

    /// <summary>Deletes the runs finished before <paramref name="finishedBefore"/>. Returns how many were deleted.</summary>
    public async Task<int> PurgeRunsAsync(DateTimeOffset finishedBefore, CancellationToken cancellationToken = default)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await Store.DeleteFinishedRunsAsync(finishedBefore, 1000, cancellationToken).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted >= 1000);

        return total;
    }

    /// <summary>
    /// Sends an event: resumes the runs waiting for it and fires the matching automations with an
    /// <see cref="EventTrigger"/>. Returns the ids of the affected runs. A failing automation does not prevent the others
    /// (the error is stored in <see cref="Automation.LastError"/>).
    /// </summary>
    public Task<IReadOnlyList<string>> SendEventAsync(string eventName, object? payload = null, string? key = null, CancellationToken cancellationToken = default) =>
        SendEventCoreAsync(eventName, payload, key, null, cancellationToken);

    /// <summary>
    /// Like <see cref="SendEventAsync"/>, but only resumes the runs in <paramref name="runIds"/> that are still waiting for
    /// the event with that key: a run that starts waiting for it later (or that was not in the list) is not touched. Use it
    /// when the decision to send the event was made by looking at a specific list of runs (e.g. who can approve which
    /// ones), so that it does not extend to others. Matching automations with an <see cref="EventTrigger"/> are fired the
    /// same way as with <see cref="SendEventAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<string>> SendEventToRunsAsync(string eventName, IEnumerable<string> runIds, object? payload = null, string? key = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        return SendEventCoreAsync(eventName, payload, key, runIds.ToHashSet(StringComparer.Ordinal), cancellationToken);
    }

    private async Task<IReadOnlyList<string>> SendEventCoreAsync(string eventName, object? payload, string? key, HashSet<string>? onlyRuns, CancellationToken cancellationToken)
    {
        var element = payload is JsonElement e ? e.Clone() : JsonSerializer.SerializeToElement(payload, JsonSchema.DefaultSerializerOptions);
        var affected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Con una lista cerrada no se consulta el almacén: cada entrega vuelve a comprobar, bajo el candado de la
        // ejecución, que siga esperando este evento con esta clave.
        foreach (var runId in onlyRuns ?? [])
        {
            if (await DeliverEventAsync(runId, eventName, key, element, cancellationToken).ConfigureAwait(false) is { } run)
            {
                Background(run);
                affected.Add(run.Id);
            }
        }

        while (onlyRuns is null)
        {
            var page = await Store.ListRunsWaitingForEventAsync(eventName, key, _options.BatchSize, cancellationToken).ConfigureAwait(false);
            var progressed = false;
            foreach (var waiting in page)
            {
                if (seen.Add(waiting.Id) && await DeliverEventAsync(waiting.Id, eventName, key, element, cancellationToken).ConfigureAwait(false) is { } run)
                {
                    Background(run);
                    affected.Add(run.Id);
                    progressed = true;
                }
            }

            if (!progressed || page.Count < _options.BatchSize)
            {
                break;
            }
        }

        foreach (var automation in await Store.ListAutomationsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (automation.Enabled && automation.Trigger is EventTrigger trigger && trigger.Matches(eventName, element))
            {
                try
                {
                    var run = await FireAsync(automation, element, $"event:{eventName}", null, cancellationToken).ConfigureAwait(false);
                    affected.Add(run.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    await RecordFailureAsync(automation, ex, Now, reschedule: false).ConfigureAwait(false);
                }
            }
        }

        return affected;
    }

    private async Task<WorkflowRun?> DeliverEventAsync(string runId, string eventName, string? key, JsonElement payload, CancellationToken cancellationToken)
    {
        using var gate = await AcquireRunLockAsync(runId, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var run = await Store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null || !WorkflowRunQueries.IsWaitingFor(run, eventName, key))
            {
                return null;
            }

            var stepKey = run.Waiting!.StepKey;
            run.Steps[stepKey] = new StepRecord(stepKey, payload, 1, Now);
            run.Waiting = null;
            run.ResumeAt = null;
            run.Status = WorkflowStatus.Pending;
            Lease(run);
            run.Logs.Add($"{Now:O} Evento recibido: {eventName}{(key is null ? string.Empty : $" ({key})")}");
            try
            {
                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
                return run;
            }
            catch (WorkflowConcurrencyException)
            {
                // Otra réplica o un vencimiento la modificó: se vuelve a comprobar.
            }
        }

        return null;
    }

    private async Task<WorkflowRun> GetRequiredAsync(string runId, CancellationToken cancellationToken) =>
        await Store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la ejecución '{runId}'.");

    /// <summary>Runs in the background; does not duplicate if a run is already queued for the same saved version.</summary>
    private WorkflowRun Background(WorkflowRun run)
    {
        var key = $"{run.Id}@{run.Version}";
        var runId = run.Id;
        var version = run.Version;
        var start = new TaskCompletionSource();
        var task = start.Task.ContinueWith(
            async _ =>
            {
                try
                {
                    await ExecuteAsync(runId, version, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Un fallo del almacén no debe romper WhenIdleAsync: la ejecución se retoma en un ciclo posterior.
                    ReportError(ex, $"Error al ejecutar '{runId}' en segundo plano");
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

    /// <summary>
    /// Executes (or resumes) a run. With <paramref name="expectedVersion"/>, it only acts if the run is still at the version
    /// with which it was decided to execute it (another path may have handled it in the meantime).
    /// </summary>
    private async Task<WorkflowRun> ExecuteAsync(string runId, long? expectedVersion, CancellationToken cancellationToken)
    {
        using var gate = await AcquireRunLockAsync(runId, cancellationToken).ConfigureAwait(false);
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await GetRequiredAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run.IsFinished || (expectedVersion is { } version && run.Version != version) || IsLeasedByOther(run))
            {
                return run;
            }

            if (!_workflows.TryGetValue(run.WorkflowName, out var workflow))
            {
                run.Status = WorkflowStatus.Failed;
                run.Error = $"El workflow '{run.WorkflowName}' no está registrado.";
                run.CompletedAt = Now;
                ReleaseLease(run);
                return await TrySaveAsync(run, cancellationToken).ConfigureAwait(false);
            }

            // Reclamo: si otra réplica la tomó a la vez, este guardado falla y se le deja a ella.
            run.Status = WorkflowStatus.Running;
            run.Executions++;
            run.ResumeAt = null;
            Lease(run);
            try
            {
                await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkflowConcurrencyException)
            {
                return await GetRequiredAsync(runId, CancellationToken.None).ConfigureAwait(false);
            }

            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _executing[runId] = execution;
            try
            {
                return await RunWorkflowAsync(workflow, run, execution, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _executing.TryRemove(runId, out _);
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<WorkflowRun> RunWorkflowAsync(IWorkflow workflow, WorkflowRun run, CancellationTokenSource execution, CancellationToken cancellationToken)
    {
        var context = new WorkflowContext(run, this, Services, execution.Token);
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = RenewLeaseAsync(context, execution, stopHeartbeat.Token);
        Action<WorkflowRun> outcome;
        try
        {
            var output = await workflow.ExecuteAsync(context, run.Input).ConfigureAwait(false);
            var element = JsonSerializer.SerializeToElement(output, JsonSchema.DefaultSerializerOptions);
            outcome = r =>
            {
                r.Output = element;
                r.Status = WorkflowStatus.Completed;
                r.CompletedAt = Now;
            };
        }
        catch (WorkflowSuspendedException suspended)
        {
            outcome = r =>
            {
                r.Status = suspended.Status;
                r.ResumeAt = suspended.ResumeAt;
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Apagado del proceso: queda pendiente y sin reclamo, así se reanuda al volver (aquí o en otra réplica).
            outcome = r => r.Status = WorkflowStatus.Pending;
        }
        catch (Exception ex) when (ex is not WorkflowConcurrencyException && !execution.IsCancellationRequested)
        {
            outcome = r =>
            {
                r.Status = WorkflowStatus.Failed;
                r.Error = ex.Message;
                r.Logs.Add($"{Now:O} Error: {ex}");
                r.CompletedAt = Now;
            };
        }
        catch (Exception)
        {
            // Cancelada (CancelAsync) o reclamada por otra instancia: el estado guardado manda y no se sobrescribe.
            outcome = null!;
        }
        finally
        {
            await stopHeartbeat.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        if (outcome is null || (execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
        {
            return await GetRequiredAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            var saved = await context.PersistAsync(r =>
            {
                outcome(r);
                ReleaseLease(r);
            }).ConfigureAwait(false);
            await NotifyAsync(saved).ConfigureAwait(false);
            return saved;
        }
        catch (WorkflowConcurrencyException)
        {
            return await GetRequiredAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Renews the claim while the run progresses; if another instance took it over (or it was cancelled), stops it.</summary>
    private async Task RenewLeaseAsync(WorkflowContext context, CancellationTokenSource execution, CancellationToken stop)
    {
        var period = _options.LeaseDuration / 4;
        try
        {
            while (true)
            {
                await Task.Delay(period, _options.TimeProvider, stop).ConfigureAwait(false);
                try
                {
                    await context.PersistAsync(r => r.LeaseExpiresAt = Now + _options.LeaseDuration).ConfigureAwait(false);
                }
                catch (WorkflowConcurrencyException)
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ReportError(ex, $"No se pudo renovar el reclamo de '{context.RunId}'");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsLeasedByOther(WorkflowRun run) =>
        run.LeaseOwner is { } owner && owner != InstanceId && run.LeaseExpiresAt is { } expires && expires > Now;

    private void Lease(WorkflowRun run)
    {
        run.LeaseOwner = InstanceId;
        run.LeaseExpiresAt = Now + _options.LeaseDuration;
    }

    private static void ReleaseLease(WorkflowRun run)
    {
        run.LeaseOwner = null;
        run.LeaseExpiresAt = null;
    }

    private async Task<WorkflowRun> TrySaveAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        try
        {
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            return run;
        }
        catch (WorkflowConcurrencyException)
        {
            return await GetRequiredAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        run.UpdatedAt = Now;
        await Store.SaveRunAsync(run, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(run).ConfigureAwait(false);
    }

    private Task NotifyAsync(WorkflowRun run) => _options.OnRunChanged is { } callback ? callback(run) : Task.CompletedTask;

    private void ReportError(Exception error, string message)
    {
        try
        {
            _options.OnError?.Invoke(error, message);
        }
        catch (Exception)
        {
            // El registro de errores no puede detener el motor.
        }
    }

    // ---------------------------------------------------------------------
    // Automatizaciones
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates or updates an automation (computes its next run). Validates the trigger when creating it: existing cron
    /// expression and time zone, an interval of at least one second and a required secret for webhooks.
    /// Keeps the <see cref="Automation.LastRunAt"/>, <see cref="Automation.LastRunId"/> and creation date of the existing one.
    /// </summary>
    public async Task<Automation> AddAutomationAsync(Automation automation, CancellationToken cancellationToken = default)
    {
        if (!_workflows.ContainsKey(automation.Workflow))
        {
            throw new ArgumentException($"El workflow '{automation.Workflow}' no está registrado.");
        }

        automation.Trigger.Validate();
        for (var attempt = 1; ; attempt++)
        {
            var existing = await Store.GetAutomationAsync(automation.Id, cancellationToken).ConfigureAwait(false);
            var lastRunAt = automation.LastRunAt ?? existing?.LastRunAt;
            var candidate = automation with
            {
                Version = existing?.Version ?? 0,
                CreatedAt = existing?.CreatedAt ?? automation.CreatedAt,
                LastRunAt = lastRunAt,
                LastRunId = automation.LastRunId ?? existing?.LastRunId,
                NextRunAt = automation.Trigger.NextOccurrence(Now, lastRunAt),
            };
            try
            {
                return await Store.SaveAutomationAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkflowConcurrencyException) when (attempt < 10)
            {
                // Otra réplica la guardó a la vez (p. ej. al arrancar): se vuelve a leer.
            }
        }
    }

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default) => Store.ListAutomationsAsync(cancellationToken);

    public Task RemoveAutomationAsync(string id, CancellationToken cancellationToken = default) => Store.DeleteAutomationAsync(id, cancellationToken);

    /// <summary>Enables or disables an automation. Enabling it clears its previous failures.</summary>
    public async Task<Automation?> SetAutomationEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        if (await Store.GetAutomationAsync(id, cancellationToken).ConfigureAwait(false) is not { } automation)
        {
            return null;
        }

        return await AddAutomationAsync(
            enabled ? automation with { Enabled = true, ConsecutiveFailures = 0, LastError = null } : automation with { Enabled = false },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an automation now (manually), in the background.</summary>
    public async Task<WorkflowRun> RunAutomationNowAsync(string id, CancellationToken cancellationToken = default)
    {
        var automation = await Store.GetAutomationAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la automatización '{id}'.");
        return await FireAsync(automation, null, $"manual:{id}", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a webhook with the <c>X-Webhook-Secret</c> or <c>X-Signature-256</c> headers (no timestamp).
    /// Only works for <see cref="WebhookVerification.SharedSecret"/> and <see cref="WebhookVerification.BodySignature"/>;
    /// for the timestamped signature use <see cref="TriggerWebhookAsync(string, WebhookRequest, CancellationToken)"/>.
    /// </summary>
    public Task<WorkflowRun> TriggerWebhookAsync(string automationId, ReadOnlyMemory<byte> body, string? secretHeader = null, string? signatureHeader = null, CancellationToken cancellationToken = default) =>
        TriggerWebhookAsync(automationId, new WebhookRequest(body) { Secret = secretHeader, Signature = signatureHeader }, cancellationToken);

    /// <summary>
    /// Handles a webhook: authenticates it according to <see cref="WebhookTrigger.Verification"/> (throws
    /// <see cref="UnauthorizedAccessException"/> if it is not valid) and fires the workflow with the body as input. A
    /// repeated request (same signature or same delivery id) returns the run already created instead of creating another.
    /// </summary>
    public async Task<WorkflowRun> TriggerWebhookAsync(string automationId, WebhookRequest request, CancellationToken cancellationToken = default)
    {
        var automation = await Store.GetAutomationAsync(automationId, cancellationToken).ConfigureAwait(false);
        if (automation is not { Enabled: true, Trigger: WebhookTrigger trigger })
        {
            throw new KeyNotFoundException($"No existe un webhook activo '{automationId}'.");
        }

        var replayKey = Authenticate(trigger, request);
        var deliveryKey = request.DeliveryId is { } delivery ? $"{automationId}\n{delivery}" : null;
        if (deliveryKey is not null && _deliveries.TryGet(deliveryKey, Now) is { } previousRunId
            && await Store.GetRunAsync(previousRunId, cancellationToken).ConfigureAwait(false) is { } previous)
        {
            return previous; // Reintento del emisor con el mismo id de entrega.
        }

        // Ejecución con id determinista: una repetición (aquí o en otra réplica) devuelve la misma ejecución.
        var dedupe = replayKey ?? request.DeliveryId;
        var runId = dedupe is null ? null : "hook_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{automationId}\n{dedupe}")))[..32];

        JsonElement payload;
        try
        {
            payload = request.Body.Length == 0 ? default : JsonDocument.Parse(request.Body).RootElement.Clone();
        }
        catch (JsonException)
        {
            payload = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(request.Body.Span));
        }

        WorkflowRun run;
        try
        {
            run = await FireAsync(automation, payload, $"webhook:{automationId}", runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(automation, ex, Now, reschedule: false).ConfigureAwait(false);
            throw;
        }

        if (deliveryKey is not null)
        {
            _deliveries.Add(deliveryKey, run.Id, Now + TimeSpan.FromHours(24));
        }

        return run;
    }

    /// <summary>Authenticates the request. Returns the anti-replay key (the timestamped signature) or null.</summary>
    private string? Authenticate(WebhookTrigger trigger, WebhookRequest request)
    {
        if (trigger.Secret is not { Length: > 0 } secret)
        {
            return trigger.AllowUnauthenticated ? null : throw new UnauthorizedAccessException("El webhook no tiene secreto configurado.");
        }

        var body = request.Body.Span;
        switch (trigger.Verification)
        {
            case WebhookVerification.TimestampedSignature:
                if (!long.TryParse(request.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
                    || Math.Abs((Now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(timestamp, 0, 253402300799))).Ticks) > _options.WebhookTolerance.Ticks)
                {
                    throw new UnauthorizedAccessException($"Falta la cabecera {WebhookSignature.TimestampHeader} o está fuera de la tolerancia.");
                }

                var expected = WebhookSignature.Sign(secret, timestamp, body);
                return WebhookSignature.Matches(expected, request.Signature)
                    ? expected
                    : throw new UnauthorizedAccessException("Firma del webhook inválida.");

            case WebhookVerification.BodySignature:
                return WebhookSignature.Matches(WebhookSignature.SignBody(secret, body), request.Signature)
                    ? null
                    : throw new UnauthorizedAccessException("Firma del webhook inválida.");

            default:
                return request.Secret is not null
                       && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Secret), Encoding.UTF8.GetBytes(secret))
                    ? null
                    : throw new UnauthorizedAccessException("Secreto del webhook inválido.");
        }
    }

    /// <summary>
    /// Checks the secret or the HMAC-SHA256 signature of the <b>body only</b> of a webhook in constant time (without replay
    /// protection). For the timestamped signature use <see cref="WebhookSignature.Sign"/>.
    /// </summary>
    public static bool VerifyWebhook(string secret, ReadOnlySpan<byte> body, string? secretHeader, string? signatureHeader)
    {
        if (secretHeader is not null)
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secretHeader), Encoding.UTF8.GetBytes(secret));
        }

        return WebhookSignature.Matches(WebhookSignature.SignBody(secret, body), signatureHeader);
    }

    /// <summary>
    /// Processes pending work: fires due scheduled automations, resumes runs that are sleeping, have expired waits or were
    /// abandoned by a crashed instance, and applies retention. Called periodically by the background service; it can also
    /// be called manually. Each automation is isolated: if one fails, the others still fire.
    /// </summary>
    public async Task TickAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var current = now ?? Now;

        foreach (var automation in await Store.ListDueAutomationsAsync(current, _options.BatchSize, cancellationToken).ConfigureAwait(false))
        {
            if (!WorkflowRunQueries.IsDue(automation, current))
            {
                continue;
            }

            try
            {
                await FireScheduledAsync(automation, current, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecordFailureAsync(automation, ex, current, reschedule: true).ConfigureAwait(false);
            }
        }

        foreach (var run in await Store.ListDueRunsAsync(current, _options.BatchSize, cancellationToken).ConfigureAwait(false))
        {
            Background(run);
        }

        if (_options.RunRetention is { } retention && current - _lastPurge >= PurgeInterval)
        {
            _lastPurge = current;
            try
            {
                await PurgeRunsAsync(current - retention, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportError(ex, "Error al borrar ejecuciones antiguas");
            }
        }
    }

    /// <summary>
    /// Resumes runs left halfway (pending or running without a valid claim) and due runs. Those claimed by another
    /// instance are taken over in <see cref="TickAsync"/> when their claim expires.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var run in await Store.ListDueRunsAsync(Now, _options.BatchSize, cancellationToken).ConfigureAwait(false))
        {
            Background(run);
        }
    }

    /// <summary>
    /// Fires a scheduled occurrence. The next occurrence is computed before creating the run (a nonexistent time zone
    /// fails here, without creating anything) and the run has a deterministic id per occurrence, so two replicas (or a
    /// retry after a crash) get the same one.
    /// </summary>
    private async Task FireScheduledAsync(Automation automation, DateTimeOffset current, CancellationToken cancellationToken)
    {
        if (!_workflows.ContainsKey(automation.Workflow))
        {
            throw new InvalidOperationException($"El workflow '{automation.Workflow}' de la automatización '{automation.Id}' no está registrado.");
        }

        var due = automation.NextRunAt!.Value;
        var next = automation.Trigger.NextOccurrence(current, current);
        var runId = $"auto_{automation.Id}_{due.ToUnixTimeMilliseconds()}";
        var run = await StartAsync(automation.Workflow, Input(automation, null), runId, $"schedule:{automation.Id}", wait: false, cancellationToken).ConfigureAwait(false);
        try
        {
            await Store.SaveAutomationAsync(automation with
            {
                LastRunAt = current,
                LastRunId = run.Id,
                NextRunAt = next,
                Enabled = automation.Enabled && automation.Trigger is not OnceTrigger,
                ConsecutiveFailures = 0,
                LastError = null,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowConcurrencyException)
        {
            // Otra réplica registró esta misma ocurrencia (y obtuvo la misma ejecución).
        }
    }

    private async Task<WorkflowRun> FireAsync(Automation automation, JsonElement? payload, string triggeredBy, string? runId, CancellationToken cancellationToken)
    {
        var run = await StartAsync(automation.Workflow, Input(automation, payload), runId, triggeredBy, wait: false, cancellationToken).ConfigureAwait(false);
        await UpdateAutomationAsync(automation.Id, a => a with { LastRunAt = Now, LastRunId = run.Id, ConsecutiveFailures = 0, LastError = null }).ConfigureAwait(false);
        return run;
    }

    private static JsonElement Input(Automation automation, JsonElement? payload) =>
        automation.Input ?? (payload is { ValueKind: not JsonValueKind.Undefined } p ? p : JsonSerializer.SerializeToElement(new { }));

    /// <summary>
    /// Records a failure: postpones the next attempt with exponential backoff (if it is scheduled) and disables the
    /// automation when <see cref="WorkflowEngineOptions.MaxAutomationFailures"/> is reached. Never throws.
    /// </summary>
    private async Task RecordFailureAsync(Automation automation, Exception error, DateTimeOffset current, bool reschedule)
    {
        ReportError(error, $"La automatización '{automation.Id}' falló");
        var failures = automation.ConsecutiveFailures + 1;
        var disable = failures >= _options.MaxAutomationFailures;
        var delay = TimeSpan.FromTicks(Math.Min(
            _options.MaxAutomationRetryDelay.Ticks,
            _options.AutomationRetryDelay.Ticks * (1L << Math.Min(failures - 1, 30))));
        try
        {
            await Store.SaveAutomationAsync(automation with
            {
                ConsecutiveFailures = failures,
                LastError = error.Message,
                Enabled = automation.Enabled && !disable,
                NextRunAt = disable ? null : reschedule ? current + delay : automation.NextRunAt,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Conflicto (otra réplica la atendió) o almacén caído: se reintentará en el próximo ciclo.
            ReportError(ex, $"No se pudo registrar el fallo de la automatización '{automation.Id}'");
        }
    }

    private async Task UpdateAutomationAsync(string id, Func<Automation, Automation> update)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await Store.GetAutomationAsync(id, CancellationToken.None).ConfigureAwait(false) is not { } current)
            {
                return;
            }

            try
            {
                await Store.SaveAutomationAsync(update(current), CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (WorkflowConcurrencyException)
            {
            }
        }
    }

    // ---------------------------------------------------------------------
    // Semáforos por ejecución (se eliminan al dejar de usarse)
    // ---------------------------------------------------------------------

    private async Task<RunLock> AcquireRunLockAsync(string runId, CancellationToken cancellationToken)
    {
        RunGate? gate;
        lock (_runLocksGate)
        {
            if (!_runLocks.TryGetValue(runId, out gate))
            {
                _runLocks[runId] = gate = new RunGate();
            }

            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReference(runId, gate);
            throw;
        }

        return new RunLock(this, runId, gate);
    }

    private void ReleaseReference(string runId, RunGate gate)
    {
        lock (_runLocksGate)
        {
            if (--gate.References == 0)
            {
                _runLocks.Remove(runId);
                gate.Dispose();
            }
        }
    }

    private sealed class RunGate : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private readonly struct RunLock(WorkflowEngine engine, string runId, RunGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Semaphore.Release();
            engine.ReleaseReference(runId, gate);
        }
    }

    /// <summary>Recent delivery ids (bounded): each one points to the run it created.</summary>
    private sealed class WebhookDeliveryCache(int capacity)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (string RunId, DateTimeOffset Expires)> _entries = new(StringComparer.Ordinal);

        public string? TryGet(string key, DateTimeOffset now)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(key, out var entry) && entry.Expires > now ? entry.RunId : null;
            }
        }

        public void Add(string key, string runId, DateTimeOffset expires)
        {
            lock (_gate)
            {
                if (_entries.Count >= capacity && !_entries.ContainsKey(key))
                {
                    // Se quitan primero las vencidas y, si no basta, las más antiguas.
                    var now = expires - TimeSpan.FromHours(24);
                    foreach (var old in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                    {
                        _entries.Remove(old);
                    }

                    foreach (var old in _entries.OrderBy(e => e.Value.Expires).Take(Math.Max(0, _entries.Count - (capacity * 9 / 10))).Select(e => e.Key).ToList())
                    {
                        _entries.Remove(old);
                    }
                }

                _entries[key] = (runId, expires);
            }
        }
    }
}
