using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>
/// API available inside a workflow: durable steps, waits, events and agents.
/// </summary>
public sealed class WorkflowContext
{
    private readonly WorkflowRun _run;
    private readonly WorkflowEngine _engine;
    private readonly Dictionary<string, int> _occurrences = [];
    private readonly Lock _gate = new();

    // Los guardados se encadenan: cada uno parte de la versión que dejó el anterior (pasos en paralelo, renovación del reclamo).
    private Task _lastSave = Task.CompletedTask;

    internal WorkflowContext(WorkflowRun run, WorkflowEngine engine, IServiceProvider? services, CancellationToken cancellationToken)
    {
        _run = run;
        _engine = engine;
        Services = services;
        CancellationToken = cancellationToken;
    }

    public string RunId => _run.Id;

    public string WorkflowName => _run.WorkflowName;

    public string? TriggeredBy => _run.TriggeredBy;

    public IServiceProvider? Services { get; }

    /// <summary>Cancelled when the process shuts down, when the run is cancelled (<see cref="WorkflowEngine.CancelAsync"/>) or if another instance claimed it.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Runs a step only once: if it was already completed in a previous execution, returns the saved result without
    /// running it again. Retries with exponential backoff according to <paramref name="options"/>.
    /// Repeated names (e.g. in loops) are numbered automatically.
    /// </summary>
    public async Task<T> StepAsync<T>(string name, Func<CancellationToken, Task<T>> run, StepOptions? options = null)
    {
        var key = Key(name);
        if (TryGetStep(key, out var record))
        {
            return record.Output is { } output ? output.Deserialize<T>(JsonSchema.DefaultSerializerOptions)! : default!;
        }

        // Una ejecución cancelada no empieza pasos nuevos.
        CancellationToken.ThrowIfCancellationRequested();
        options ??= StepOptions.Default;
        var delay = options.InitialDelay;
        for (var attempt = 1; ; attempt++)
        {
            T value;
            try
            {
                if (options.Timeout is { } timeout)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
                    cts.CancelAfter(timeout);
                    value = await run(cts.Token).ConfigureAwait(false);
                }
                else
                {
                    value = await run(CancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not WorkflowSuspendedException and not WorkflowConcurrencyException
                                       && !CancellationToken.IsCancellationRequested
                                       && attempt < options.MaxAttempts
                                       && ex is not NonRetryableException
                                       && (options.ShouldRetry?.Invoke(ex) ?? true))
            {
                Log($"Paso '{key}' falló (intento {attempt}/{options.MaxAttempts}): {ex.Message}");
                await Task.Delay(delay, _engine.TimeProvider, CancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * options.BackoffFactor);
                continue;
            }

            // El guardado va fuera del try: un conflicto de versión (ejecución cancelada o reclamada por otra
            // instancia) no debe provocar que el paso se repita.
            var element = JsonSerializer.SerializeToElement(value, JsonSchema.DefaultSerializerOptions);
            await PersistAsync(r => r.Steps[key] = new StepRecord(key, element, attempt, _engine.Now)).ConfigureAwait(false);
            return value;
        }
    }

    /// <inheritdoc cref="StepAsync{T}(string, Func{CancellationToken, Task{T}}, StepOptions?)"/>
    public Task StepAsync(string name, Func<CancellationToken, Task> run, StepOptions? options = null) =>
        StepAsync<bool>(name, async ct =>
        {
            await run(ct).ConfigureAwait(false);
            return true;
        }, options);

    /// <summary>
    /// Runs an agent as a durable step and returns its final text. <paramref name="context"/> reaches the agent's tools
    /// as <see cref="ToolExecutionContext.Context"/> (e.g. the id of the user who owns the task).
    /// </summary>
    public Task<string> RunAgentAsync(IAgent agent, string prompt, string? stepName = null, StepOptions? options = null, IReadOnlyDictionary<string, object?>? context = null) =>
        StepAsync(stepName ?? $"agent:{agent.Name}", async ct =>
        {
            var result = await agent.GenerateAsync(new AgentCallOptions { Prompt = prompt, Services = Services, Context = context, CancellationToken = ct }).ConfigureAwait(false);
            return result.Text;
        }, options);

    /// <summary>Runs a multi-agent orchestration as a durable step.</summary>
    public Task<string> RunAsync(IOrchestrationNode node, string input, string? stepName = null, StepOptions? options = null) =>
        StepAsync(stepName ?? $"node:{node.Name}", ct => node.RunAsync(input, new OrchestrationContext(new OrchestrationOptions
        {
            Services = Services,
            CancellationToken = ct,
        })), options);

    /// <summary>
    /// Sleeps durably: the run is suspended and the engine resumes it when the time is up
    /// (even if the process was restarted in the meantime).
    /// </summary>
    public Task SleepAsync(string name, TimeSpan duration) => SleepUntilInternalAsync(name, () => _engine.Now + duration);

    /// <summary>Sleeps until a given date.</summary>
    public Task SleepUntilAsync(string name, DateTimeOffset until) => SleepUntilInternalAsync(name, () => until);

    private async Task SleepUntilInternalAsync(string name, Func<DateTimeOffset> until)
    {
        var key = "sleep:" + Key(name);
        if (TryGetStep(key, out var record))
        {
            var wakeAt = record.Output!.Value.GetDateTimeOffset();
            if (_engine.Now >= wakeAt)
            {
                return;
            }

            throw new WorkflowSuspendedException(WorkflowStatus.Sleeping, wakeAt);
        }

        var target = until();
        await PersistAsync(r => r.Steps[key] = new StepRecord(key, JsonSerializer.SerializeToElement(target), 1, _engine.Now)).ConfigureAwait(false);
        if (_engine.Now < target)
        {
            throw new WorkflowSuspendedException(WorkflowStatus.Sleeping, target);
        }
    }

    /// <summary>
    /// Waits for an external event sent with <see cref="WorkflowEngine.SendEventAsync"/> (human approval, payment
    /// webhook, a customer's reply...). Returns the event payload, or <c>default</c> if <paramref name="timeout"/>
    /// elapses. Use <paramref name="key"/> to tell instances apart (e.g. the order id).
    /// </summary>
    public Task<T?> WaitForEventAsync<T>(string eventName, string? key = null, TimeSpan? timeout = null)
    {
        var stepKey = Key($"event:{eventName}" + (key is null ? string.Empty : $":{key}"));
        if (TryGetStep(stepKey, out var record))
        {
            return Task.FromResult(record.TimedOut || record.Output is null
                ? default
                : record.Output.Value.Deserialize<T>(JsonSchema.DefaultSerializerOptions));
        }

        lock (_gate)
        {
            if (_run.Waiting is { } waiting && waiting.StepKey == stepKey)
            {
                if (waiting.TimeoutAt is { } at && _engine.Now >= at)
                {
                    _run.Steps[stepKey] = new StepRecord(stepKey, null, 1, _engine.Now) { TimedOut = true };
                    _run.Waiting = null;
                    return Task.FromResult<T?>(default);
                }

                throw new WorkflowSuspendedException(WorkflowStatus.WaitingForEvent, waiting.TimeoutAt);
            }

            var timeoutAt = timeout is { } t ? _engine.Now + t : (DateTimeOffset?)null;
            _run.Waiting = new EventWait(eventName, key, stepKey, timeoutAt);
            throw new WorkflowSuspendedException(WorkflowStatus.WaitingForEvent, timeoutAt);
        }
    }

    /// <summary>Adds a line to the run's log.</summary>
    public void Log(string message)
    {
        lock (_gate)
        {
            _run.Logs.Add($"{_engine.Now:O} {message}");
        }
    }

    private string Key(string name)
    {
        lock (_gate)
        {
            var count = _occurrences.GetValueOrDefault(name) + 1;
            _occurrences[name] = count;
            return count == 1 ? name : $"{name}#{count}";
        }
    }

    private bool TryGetStep(string key, out StepRecord record)
    {
        lock (_gate)
        {
            return _run.Steps.TryGetValue(key, out record!);
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> and saves a copy with optimistic concurrency. Saves happen one at a time, so each
    /// copy carries the version of the previous one. Throws <see cref="WorkflowConcurrencyException"/> if another instance
    /// (or a cancellation) changed the run.
    /// </summary>
    internal Task<WorkflowRun> PersistAsync(Action<WorkflowRun>? mutate)
    {
        lock (_gate)
        {
            var previous = _lastSave;
            var save = SaveAfterAsync(previous, mutate);
            _lastSave = save;
            return save;
        }
    }

    private async Task<WorkflowRun> SaveAfterAsync(Task previous, Action<WorkflowRun>? mutate)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // El error del guardado anterior ya lo recibió quien lo pidió.
        }

        WorkflowRun snapshot;
        lock (_gate)
        {
            mutate?.Invoke(_run);
            _run.UpdatedAt = _engine.Now;
            snapshot = _run.Clone();
        }

        await _engine.Store.SaveRunAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _run.Version = snapshot.Version;
        }

        return snapshot;
    }
}
