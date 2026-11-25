using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>
/// Persistence of runs and automations. Implement this interface for SQL, Redis, Cosmos...
/// </summary>
/// <remarks>
/// <para>
/// <b>Optimistic concurrency</b> (required for multiple replicas on the same store): <see cref="SaveRunAsync"/> and
/// <see cref="SaveAutomationAsync"/> only save if the received <c>Version</c> matches the stored one (0 = does not exist)
/// and increment it; if it does not match, they throw <see cref="WorkflowConcurrencyException"/>. In SQL: <c>UPDATE ... WHERE id = @id
/// AND version = @version</c> (0 rows = conflict) and <c>INSERT</c> with a primary key for new ones.
/// </para>
/// <para>
/// <b>Indexed queries</b>: the engine does not list the whole history on every cycle; it uses <see cref="ListDueRunsAsync"/>,
/// <see cref="ListRunsWaitingForEventAsync"/> and <see cref="ListDueAutomationsAsync"/>, whose criteria are in
/// <see cref="WorkflowRunQueries"/>. Consider indexing status, <c>ResumeAt</c>, <c>LeaseExpiresAt</c> and the awaited event.
/// </para>
/// </remarks>
public interface IWorkflowStore
{
    /// <summary>
    /// Saves the run if <see cref="WorkflowRun.Version"/> matches the stored one (0 = new, must not exist) and leaves the
    /// new version in <paramref name="run"/>. If it does not match, throws <see cref="WorkflowConcurrencyException"/>.
    /// </summary>
    Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default);

    Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Filtered runs (most recent first).</summary>
    Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Runs the engine must handle at <paramref name="now"/> (<see cref="WorkflowRunQueries.IsDue(WorkflowRun, DateTimeOffset)"/>), most overdue first.</summary>
    Task<IReadOnlyList<WorkflowRun>> ListDueRunsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Runs waiting for the event (<see cref="WorkflowRunQueries.IsWaitingFor"/>).</summary>
    Task<IReadOnlyList<WorkflowRun>> ListRunsWaitingForEventAsync(string eventName, string? key, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Deletes up to <paramref name="limit"/> runs finished before <paramref name="finishedBefore"/>. Returns how many were deleted.</summary>
    Task<int> DeleteFinishedRunsAsync(DateTimeOffset finishedBefore, int limit = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the automation if <see cref="Automation.Version"/> matches the stored one (0 = new) and returns the saved copy
    /// with the new version. If it does not match, throws <see cref="WorkflowConcurrencyException"/>.
    /// </summary>
    Task<Automation> SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default);

    Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Enabled automations whose <see cref="Automation.NextRunAt"/> is due (<see cref="WorkflowRunQueries.IsDue(Automation, DateTimeOffset)"/>).</summary>
    Task<IReadOnlyList<Automation>> ListDueAutomationsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default);

    Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Criteria of the engine's queries (for implementing <see cref="IWorkflowStore"/>).</summary>
public static class WorkflowRunQueries
{
    /// <summary>
    /// A run is due if it is sleeping or waiting for an event with <see cref="WorkflowRun.ResumeAt"/> &lt;= <paramref name="now"/>,
    /// or if it is pending or running without a valid claim (<see cref="WorkflowRun.LeaseExpiresAt"/> null or expired: the
    /// instance that was running it died or shut down).
    /// </summary>
    public static bool IsDue(WorkflowRun run, DateTimeOffset now) => run.Status switch
    {
        WorkflowStatus.Sleeping or WorkflowStatus.WaitingForEvent => run.ResumeAt is { } resumeAt && resumeAt <= now,
        WorkflowStatus.Pending or WorkflowStatus.Running => run.LeaseExpiresAt is not { } lease || lease <= now,
        _ => false,
    };

    /// <summary>Since when it has been due (to handle the most overdue first).</summary>
    public static DateTimeOffset DueAt(WorkflowRun run) =>
        run.Status is WorkflowStatus.Sleeping or WorkflowStatus.WaitingForEvent
            ? run.ResumeAt ?? run.UpdatedAt
            : run.LeaseExpiresAt ?? run.UpdatedAt;

    /// <summary>The run is waiting for <paramref name="eventName"/> with no key or with the key <paramref name="key"/>.</summary>
    public static bool IsWaitingFor(WorkflowRun run, string eventName, string? key) =>
        run.Status == WorkflowStatus.WaitingForEvent
        && run.Waiting is { } wait
        && wait.EventName == eventName
        && (wait.Key is null || wait.Key == key);

    /// <summary>Enabled and with <see cref="Automation.NextRunAt"/> &lt;= <paramref name="now"/>.</summary>
    public static bool IsDue(Automation automation, DateTimeOffset now) => automation.Enabled && automation.NextRunAt is { } next && next <= now;

    /// <summary>When it finished (for retention).</summary>
    public static DateTimeOffset FinishedAt(WorkflowRun run) => run.CompletedAt ?? run.UpdatedAt;

    internal static WorkflowConcurrencyException Conflict(string kind, string id, long expected, long stored) =>
        new($"La {kind} '{id}' cambió en el almacén (versión {stored}, se esperaba {expected}).");

    /// <summary>Lightweight copy for indexes: without steps, log, input or output.</summary>
    internal static WorkflowRun Projection(WorkflowRun run) => new()
    {
        Id = run.Id,
        WorkflowName = run.WorkflowName,
        Status = run.Status,
        Input = EmptyInput,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
        CompletedAt = run.CompletedAt,
        ResumeAt = run.ResumeAt,
        Waiting = run.Waiting,
        TriggeredBy = run.TriggeredBy,
        Version = run.Version,
        LeaseOwner = run.LeaseOwner,
        LeaseExpiresAt = run.LeaseExpiresAt,
    };

    private static readonly JsonElement EmptyInput = JsonSerializer.SerializeToElement(new { });
}

/// <summary>
/// In-memory store (testing and development; lost on restart). Keeps at most <see cref="MaxFinishedRuns"/> finished
/// runs: when exceeded, it discards the oldest ones.
/// </summary>
public sealed class InMemoryWorkflowStore(int maxFinishedRuns = 10_000) : IWorkflowStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Automation> _automations = new(StringComparer.Ordinal);
    private int _finished;

    /// <summary>Maximum number of finished runs kept.</summary>
    public int MaxFinishedRuns { get; } = maxFinishedRuns > 0 ? maxFinishedRuns : throw new ArgumentOutOfRangeException(nameof(maxFinishedRuns));

    private sealed record StoredRun(string Json, WorkflowRun Index);

    public Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var exists = _runs.TryGetValue(run.Id, out var current);
            var stored = current?.Index.Version ?? 0;
            if (stored != run.Version)
            {
                throw WorkflowRunQueries.Conflict("ejecución", run.Id, run.Version, stored);
            }

            run.Version = stored + 1;
            string json;
            try
            {
                json = JsonSerializer.Serialize(run, JsonSchema.DefaultSerializerOptions);
            }
            catch
            {
                run.Version = stored;
                throw;
            }

            _runs[run.Id] = new StoredRun(json, WorkflowRunQueries.Projection(run));
            _finished += (run.IsFinished ? 1 : 0) - (exists && current!.Index.IsFinished ? 1 : 0);
            if (_finished > MaxFinishedRuns)
            {
                // Se recorta al 90 % para no ordenar en cada guardado.
                var excess = _finished - (MaxFinishedRuns * 9 / 10);
                foreach (var old in _runs.Values.Where(r => r.Index.IsFinished && r.Index.Id != run.Id).OrderBy(r => WorkflowRunQueries.FinishedAt(r.Index)).Take(excess).ToList())
                {
                    _runs.Remove(old.Index.Id);
                    _finished--;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_runs.TryGetValue(id, out var stored) ? Deserialize(stored) : null);
        }
    }

    public Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default) =>
        Query(r => (status is null || r.Status == status) && (workflowName is null || r.WorkflowName == workflowName), r => -r.CreatedAt.UtcTicks, limit);

    public Task<IReadOnlyList<WorkflowRun>> ListDueRunsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) =>
        Query(r => WorkflowRunQueries.IsDue(r, now), r => WorkflowRunQueries.DueAt(r).UtcTicks, limit);

    public Task<IReadOnlyList<WorkflowRun>> ListRunsWaitingForEventAsync(string eventName, string? key, int limit = 100, CancellationToken cancellationToken = default) =>
        Query(r => WorkflowRunQueries.IsWaitingFor(r, eventName, key), r => r.CreatedAt.UtcTicks, limit);

    public Task<int> DeleteFinishedRunsAsync(DateTimeOffset finishedBefore, int limit = 1000, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var old = _runs.Values.Where(r => r.Index.IsFinished && WorkflowRunQueries.FinishedAt(r.Index) < finishedBefore).Take(limit).ToList();
            foreach (var run in old)
            {
                _runs.Remove(run.Index.Id);
                _finished--;
            }

            return Task.FromResult(old.Count);
        }
    }

    private Task<IReadOnlyList<WorkflowRun>> Query(Func<WorkflowRun, bool> predicate, Func<WorkflowRun, long> order, int limit)
    {
        lock (_gate)
        {
            // Se filtra sobre el índice y solo se deserializan los resultados.
            return Task.FromResult<IReadOnlyList<WorkflowRun>>(_runs.Values
                .Where(r => predicate(r.Index))
                .OrderBy(r => order(r.Index))
                .Take(limit)
                .Select(Deserialize)
                .ToList());
        }
    }

    private static WorkflowRun Deserialize(StoredRun stored) => JsonSerializer.Deserialize<WorkflowRun>(stored.Json, JsonSchema.DefaultSerializerOptions)!;

    public Task<Automation> SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var stored = _automations.TryGetValue(automation.Id, out var current) ? current.Version : 0;
            if (stored != automation.Version)
            {
                throw WorkflowRunQueries.Conflict("automatización", automation.Id, automation.Version, stored);
            }

            var saved = Copy(automation with { Version = stored + 1 });
            _automations[automation.Id] = saved;
            return Task.FromResult(Copy(saved));
        }
    }

    public Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_automations.TryGetValue(id, out var automation) ? Copy(automation) : null);
        }
    }

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Automation>>(_automations.Values.OrderBy(a => a.CreatedAt).Select(Copy).ToList());
        }
    }

    public Task<IReadOnlyList<Automation>> ListDueAutomationsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Automation>>(_automations.Values
                .Where(a => WorkflowRunQueries.IsDue(a, now))
                .OrderBy(a => a.NextRunAt)
                .Take(limit)
                .Select(Copy)
                .ToList());
        }
    }

    public Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _automations.Remove(id);
        }

        return Task.CompletedTask;
    }

    // Copia profunda: quien la recibe no puede modificar lo guardado (p. ej. Metadata o Input).
    private static Automation Copy(Automation automation) =>
        JsonSerializer.Deserialize<Automation>(JsonSerializer.Serialize(automation, JsonSchema.DefaultSerializerOptions), JsonSchema.DefaultSerializerOptions)!;
}

/// <summary>
/// On-disk store (one JSON file per run and per automation). Survives restarts. Active runs also have a small entry in
/// <c>runs-active/</c>, so the engine's cycles do not read the whole history.
/// </summary>
/// <remarks>
/// Several processes on the <b>same machine</b> can share the directory: writes are serialized with a lock file
/// (<c>.lock</c>) and versions are checked. For several machines, use a database store, such as
/// <c>PostgresWorkflowStore</c> (package <c>AiSdk.Net.Workflows.Postgres</c>): file locking is not reliable on network
/// drives.
/// </remarks>
public sealed class FileSystemWorkflowStore : IWorkflowStore, IDisposable
{
    private const string IndexMarker = ".index-v1";

    private readonly string _runs;
    private readonly string _active;
    private readonly string _automations;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileSystemWorkflowStore(string directory)
    {
        _runs = Directory.CreateDirectory(Path.Combine(directory, "runs")).FullName;
        _active = Directory.CreateDirectory(Path.Combine(directory, "runs-active")).FullName;
        _automations = Directory.CreateDirectory(Path.Combine(directory, "automations")).FullName;
        _lockPath = Path.Combine(Path.GetFullPath(directory), ".lock");
        EnsureIndex();
    }

    public void Dispose() => _lock.Dispose();

    private static string SafeName(string id) =>
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.Contains("..", StringComparison.Ordinal)
            ? id + ".json"
            : throw new ArgumentException($"Id inválido: '{id}'.");

    private string RunPath(string id) => Path.Combine(_runs, SafeName(id));

    private string IndexPath(string id) => Path.Combine(_active, SafeName(id));

    private string AutomationPath(string id) => Path.Combine(_automations, SafeName(id));

    /// <summary>Creates the index of active runs the first time (stores created with earlier versions).</summary>
    private void EnsureIndex()
    {
        var marker = Path.Combine(_active, IndexMarker);
        if (File.Exists(marker))
        {
            return;
        }

        using var fileLock = AcquireFileLockAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (File.Exists(marker))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_runs, "*.json"))
        {
            if (TryRead<WorkflowRun>(file) is { IsFinished: false } run)
            {
                File.WriteAllText(IndexPath(run.Id), JsonSerializer.Serialize(WorkflowRunQueries.Projection(run), JsonSchema.DefaultSerializerOptions));
            }
        }

        File.WriteAllText(marker, string.Empty);
    }

    public async Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        var path = RunPath(run.Id);
        var index = IndexPath(run.Id);
        await WithLockAsync(async () =>
        {
            var stored = (await ReadAsync<WorkflowRun>(path, cancellationToken).ConfigureAwait(false))?.Version ?? 0;
            if (stored != run.Version)
            {
                throw WorkflowRunQueries.Conflict("ejecución", run.Id, run.Version, stored);
            }

            run.Version = stored + 1;
            try
            {
                if (run.IsFinished)
                {
                    await WriteAsync(path, run, cancellationToken).ConfigureAwait(false);
                    File.Delete(index);
                }
                else
                {
                    // El índice va antes: si el proceso muere entre medio, la lectura del índice se contrasta con la ejecución.
                    await WriteAsync(index, WorkflowRunQueries.Projection(run), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(path, run, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                run.Version = stored;
                throw;
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default) =>
        ReadAsync<WorkflowRun>(RunPath(id), cancellationToken);

    public async Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (status is { } active && active is not (WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled))
        {
            return await FromIndexAsync(r => r.Status == active && (workflowName is null || r.WorkflowName == workflowName), r => -r.CreatedAt.UtcTicks, limit, cancellationToken).ConfigureAwait(false);
        }

        // Historial: se recorre todo, pero solo se conservan en memoria los "limit" más recientes.
        var top = new List<WorkflowRun>();
        foreach (var file in Directory.EnumerateFiles(_runs, "*.json"))
        {
            if (await ReadAsync<WorkflowRun>(file, cancellationToken).ConfigureAwait(false) is { } run
                && (status is null || run.Status == status) && (workflowName is null || run.WorkflowName == workflowName))
            {
                top.Add(run);
                if (top.Count > limit * 2)
                {
                    top = [.. top.OrderByDescending(r => r.CreatedAt).Take(limit)];
                }
            }
        }

        return top.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public Task<IReadOnlyList<WorkflowRun>> ListDueRunsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) =>
        FromIndexAsync(r => WorkflowRunQueries.IsDue(r, now), r => WorkflowRunQueries.DueAt(r).UtcTicks, limit, cancellationToken);

    public Task<IReadOnlyList<WorkflowRun>> ListRunsWaitingForEventAsync(string eventName, string? key, int limit = 100, CancellationToken cancellationToken = default) =>
        FromIndexAsync(r => WorkflowRunQueries.IsWaitingFor(r, eventName, key), r => r.CreatedAt.UtcTicks, limit, cancellationToken);

    /// <summary>Filters with the index of active runs and confirms each candidate against the saved run.</summary>
    private async Task<IReadOnlyList<WorkflowRun>> FromIndexAsync(Func<WorkflowRun, bool> predicate, Func<WorkflowRun, long> order, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return []; // Como en memoria: el límite se comprueba después de añadir cada resultado.
        }

        var candidates = new List<WorkflowRun>();
        foreach (var file in Directory.EnumerateFiles(_active, "*.json"))
        {
            if (await ReadAsync<WorkflowRun>(file, cancellationToken).ConfigureAwait(false) is { } entry && predicate(entry))
            {
                candidates.Add(entry);
            }
        }

        var results = new List<WorkflowRun>();
        foreach (var entry in candidates.OrderBy(order))
        {
            var run = await GetRunAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            if (run is null || run.IsFinished)
            {
                await RemoveStaleIndexAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (predicate(run))
            {
                results.Add(run);
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }

        return results;
    }

    private Task<bool> RemoveStaleIndexAsync(string id, CancellationToken cancellationToken) =>
        WithLockAsync(async () =>
        {
            if (await ReadAsync<WorkflowRun>(RunPath(id), cancellationToken).ConfigureAwait(false) is null or { IsFinished: true })
            {
                File.Delete(IndexPath(id));
            }

            return true;
        }, cancellationToken);

    public async Task<int> DeleteFinishedRunsAsync(DateTimeOffset finishedBefore, int limit = 1000, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_runs, "*.json"))
        {
            // Una ejecución terminada no se vuelve a escribir: si el archivo cambió después del corte, no terminó antes.
            var id = Path.GetFileNameWithoutExtension(file);
            if (File.GetLastWriteTimeUtc(file) >= finishedBefore.UtcDateTime || File.Exists(IndexPath(id)))
            {
                continue;
            }

            var removed = await WithLockAsync(async () =>
            {
                if (await ReadAsync<WorkflowRun>(file, cancellationToken).ConfigureAwait(false) is { IsFinished: true } run && WorkflowRunQueries.FinishedAt(run) < finishedBefore)
                {
                    File.Delete(file);
                    return true;
                }

                return false;
            }, cancellationToken).ConfigureAwait(false);
            if (removed && ++deleted >= limit)
            {
                break;
            }
        }

        return deleted;
    }

    public async Task<Automation> SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default)
    {
        var path = AutomationPath(automation.Id);
        return await WithLockAsync(async () =>
        {
            var stored = (await ReadAsync<Automation>(path, cancellationToken).ConfigureAwait(false))?.Version ?? 0;
            if (stored != automation.Version)
            {
                throw WorkflowRunQueries.Conflict("automatización", automation.Id, automation.Version, stored);
            }

            var saved = automation with { Version = stored + 1 };
            await WriteAsync(path, saved, cancellationToken).ConfigureAwait(false);
            return saved;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default) =>
        ReadAsync<Automation>(AutomationPath(id), cancellationToken);

    public async Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Automation>();
        foreach (var file in Directory.EnumerateFiles(_automations, "*.json"))
        {
            if (await ReadAsync<Automation>(file, cancellationToken).ConfigureAwait(false) is { } automation)
            {
                list.Add(automation);
            }
        }

        return list.OrderBy(a => a.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<Automation>> ListDueAutomationsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) =>
        (await ListAutomationsAsync(cancellationToken).ConfigureAwait(false))
            .Where(a => WorkflowRunQueries.IsDue(a, now))
            .OrderBy(a => a.NextRunAt)
            .Take(limit)
            .ToList();

    public Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = AutomationPath(id);
        return WithLockAsync(() =>
        {
            File.Delete(path);
            return Task.FromResult(true);
        }, cancellationToken);
    }

    /// <summary>Serializes writes within the process (semaphore) and across processes (lock file).</summary>
    private async Task<T> WithLockAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // FileShare.None: exclusivo también frente a otros procesos (flock en Linux y macOS).
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 2000)
            {
                await Task.Delay(Math.Min(1 + attempt, 10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        // Escritura atómica: archivo temporal + reemplazo.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, JsonSchema.DefaultSerializerOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonSchema.DefaultSerializerOptions);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return default; // No existe o se borró entre el listado y la lectura.
        }
    }

    private static T? TryRead<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonSchema.DefaultSerializerOptions);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return default;
        }
    }
}
