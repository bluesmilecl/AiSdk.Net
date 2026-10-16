using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>
/// API disponible dentro de un workflow: pasos durables, esperas, eventos y agentes.
/// </summary>
public sealed class WorkflowContext
{
    private readonly WorkflowRun _run;
    private readonly WorkflowEngine _engine;
    private readonly Dictionary<string, int> _occurrences = [];
    private readonly Lock _gate = new();

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

    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Ejecuta un paso una sola vez: si ya se completó en una ejecución anterior, devuelve el resultado
    /// guardado sin volver a ejecutarlo. Reintenta con espera exponencial según <paramref name="options"/>.
    /// Los nombres repetidos (p. ej. en bucles) se numeran automáticamente.
    /// </summary>
    public async Task<T> StepAsync<T>(string name, Func<CancellationToken, Task<T>> run, StepOptions? options = null)
    {
        var key = Key(name);
        if (TryGetStep(key, out var record))
        {
            return record.Output is { } output ? output.Deserialize<T>(JsonSchema.DefaultSerializerOptions)! : default!;
        }

        options ??= StepOptions.Default;
        var delay = options.InitialDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                T value;
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

                var element = JsonSerializer.SerializeToElement(value, JsonSchema.DefaultSerializerOptions);
                await SaveStepAsync(new StepRecord(key, element, attempt, DateTimeOffset.UtcNow)).ConfigureAwait(false);
                return value;
            }
            catch (Exception ex) when (ex is not WorkflowSuspendedException && !CancellationToken.IsCancellationRequested
                                       && attempt < options.MaxAttempts
                                       && ex is not NonRetryableException
                                       && (options.ShouldRetry?.Invoke(ex) ?? true))
            {
                Log($"Paso '{key}' falló (intento {attempt}/{options.MaxAttempts}): {ex.Message}");
                await Task.Delay(delay, CancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * options.BackoffFactor);
            }
        }
    }

    /// <inheritdoc cref="StepAsync{T}(string, Func{CancellationToken, Task{T}}, StepOptions?)"/>
    public Task StepAsync(string name, Func<CancellationToken, Task> run, StepOptions? options = null) =>
        StepAsync<bool>(name, async ct =>
        {
            await run(ct).ConfigureAwait(false);
            return true;
        }, options);

    /// <summary>Ejecuta un agente como paso durable y devuelve su texto final.</summary>
    public Task<string> RunAgentAsync(IAgent agent, string prompt, string? stepName = null, StepOptions? options = null) =>
        StepAsync(stepName ?? $"agent:{agent.Name}", async ct =>
        {
            var result = await agent.GenerateAsync(new AgentCallOptions { Prompt = prompt, Services = Services, CancellationToken = ct }).ConfigureAwait(false);
            return result.Text;
        }, options);

    /// <summary>Ejecuta una orquestación multi-agente como paso durable.</summary>
    public Task<string> RunAsync(IOrchestrationNode node, string input, string? stepName = null, StepOptions? options = null) =>
        StepAsync(stepName ?? $"node:{node.Name}", ct => node.RunAsync(input, new OrchestrationContext(new OrchestrationOptions
        {
            Services = Services,
            CancellationToken = ct,
        })), options);

    /// <summary>
    /// Duerme de forma durable: la ejecución se suspende y el motor la reanuda al vencer el plazo
    /// (aunque el proceso se haya reiniciado entre medio).
    /// </summary>
    public Task SleepAsync(string name, TimeSpan duration) => SleepUntilInternalAsync(name, () => DateTimeOffset.UtcNow + duration);

    /// <summary>Duerme hasta una fecha.</summary>
    public Task SleepUntilAsync(string name, DateTimeOffset until) => SleepUntilInternalAsync(name, () => until);

    private async Task SleepUntilInternalAsync(string name, Func<DateTimeOffset> until)
    {
        var key = "sleep:" + Key(name);
        if (TryGetStep(key, out var record))
        {
            var wakeAt = record.Output!.Value.GetDateTimeOffset();
            if (DateTimeOffset.UtcNow >= wakeAt)
            {
                return;
            }

            throw new WorkflowSuspendedException(WorkflowStatus.Sleeping, wakeAt);
        }

        var target = until();
        await SaveStepAsync(new StepRecord(key, JsonSerializer.SerializeToElement(target), 1, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (DateTimeOffset.UtcNow < target)
        {
            throw new WorkflowSuspendedException(WorkflowStatus.Sleeping, target);
        }
    }

    /// <summary>
    /// Espera un evento externo enviado con <see cref="WorkflowEngine.SendEventAsync"/> (aprobación humana,
    /// webhook de pago, respuesta de un cliente...). Devuelve el contenido del evento o <c>default</c> si
    /// vence <paramref name="timeout"/>. Use <paramref name="key"/> para distinguir instancias (p. ej. el id del pedido).
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
                if (waiting.TimeoutAt is { } at && DateTimeOffset.UtcNow >= at)
                {
                    _run.Steps[stepKey] = new StepRecord(stepKey, null, 1, DateTimeOffset.UtcNow) { TimedOut = true };
                    _run.Waiting = null;
                    return Task.FromResult<T?>(default);
                }

                throw new WorkflowSuspendedException(WorkflowStatus.WaitingForEvent, waiting.TimeoutAt);
            }

            var timeoutAt = timeout is { } t ? DateTimeOffset.UtcNow + t : (DateTimeOffset?)null;
            _run.Waiting = new EventWait(eventName, key, stepKey, timeoutAt);
            throw new WorkflowSuspendedException(WorkflowStatus.WaitingForEvent, timeoutAt);
        }
    }

    /// <summary>Agrega una línea al registro de la ejecución.</summary>
    public void Log(string message)
    {
        lock (_gate)
        {
            _run.Logs.Add($"{DateTimeOffset.UtcNow:O} {message}");
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

    private Task SaveStepAsync(StepRecord record)
    {
        WorkflowRun snapshot;
        lock (_gate)
        {
            _run.Steps[record.Name] = record;
            _run.UpdatedAt = DateTimeOffset.UtcNow;
            snapshot = _run.Clone();
        }

        return _engine.Store.SaveRunAsync(snapshot, CancellationToken.None);
    }
}
