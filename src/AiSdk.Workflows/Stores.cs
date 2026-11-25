using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>
/// Persistencia de ejecuciones y automatizaciones. Implemente esta interfaz para SQL, Redis, Cosmos...
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrencia optimista</b> (obligatoria para varias réplicas sobre el mismo almacén): <see cref="SaveRunAsync"/> y
/// <see cref="SaveAutomationAsync"/> solo guardan si la <c>Version</c> recibida coincide con la almacenada (0 = no existe)
/// y la incrementan; si no coincide lanzan <see cref="WorkflowConcurrencyException"/>. En SQL: <c>UPDATE ... WHERE id = @id
/// AND version = @version</c> (0 filas = conflicto) e <c>INSERT</c> con clave primaria para las nuevas.
/// </para>
/// <para>
/// <b>Consultas indexadas</b>: el motor no lista todo el historial en cada ciclo; usa <see cref="ListDueRunsAsync"/>,
/// <see cref="ListRunsWaitingForEventAsync"/> y <see cref="ListDueAutomationsAsync"/>, cuyos criterios están en
/// <see cref="WorkflowRunQueries"/>. Conviene indexar estado, <c>ResumeAt</c>, <c>LeaseExpiresAt</c> y el evento esperado.
/// </para>
/// </remarks>
public interface IWorkflowStore
{
    /// <summary>
    /// Guarda la ejecución si <see cref="WorkflowRun.Version"/> coincide con la almacenada (0 = nueva, no debe existir) y
    /// deja en <paramref name="run"/> la versión nueva. Si no coincide lanza <see cref="WorkflowConcurrencyException"/>.
    /// </summary>
    Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default);

    Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Ejecuciones filtradas (más recientes primero).</summary>
    Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Ejecuciones que el motor debe atender en <paramref name="now"/> (<see cref="WorkflowRunQueries.IsDue(WorkflowRun, DateTimeOffset)"/>), las más atrasadas primero.</summary>
    Task<IReadOnlyList<WorkflowRun>> ListDueRunsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Ejecuciones que esperan el evento (<see cref="WorkflowRunQueries.IsWaitingFor"/>).</summary>
    Task<IReadOnlyList<WorkflowRun>> ListRunsWaitingForEventAsync(string eventName, string? key, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Borra hasta <paramref name="limit"/> ejecuciones terminadas antes de <paramref name="finishedBefore"/>. Devuelve cuántas borró.</summary>
    Task<int> DeleteFinishedRunsAsync(DateTimeOffset finishedBefore, int limit = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda la automatización si <see cref="Automation.Version"/> coincide con la almacenada (0 = nueva) y devuelve la
    /// copia guardada con la versión nueva. Si no coincide lanza <see cref="WorkflowConcurrencyException"/>.
    /// </summary>
    Task<Automation> SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default);

    Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Automatizaciones activas con <see cref="Automation.NextRunAt"/> vencido (<see cref="WorkflowRunQueries.IsDue(Automation, DateTimeOffset)"/>).</summary>
    Task<IReadOnlyList<Automation>> ListDueAutomationsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default);

    Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Criterios de las consultas del motor (para implementar <see cref="IWorkflowStore"/>).</summary>
public static class WorkflowRunQueries
{
    /// <summary>
    /// Una ejecución está vencida si duerme o espera un evento con <see cref="WorkflowRun.ResumeAt"/> &lt;= <paramref name="now"/>,
    /// o si está pendiente o en curso sin reclamo vigente (<see cref="WorkflowRun.LeaseExpiresAt"/> nulo o vencido: la
    /// instancia que la ejecutaba murió o se apagó).
    /// </summary>
    public static bool IsDue(WorkflowRun run, DateTimeOffset now) => run.Status switch
    {
        WorkflowStatus.Sleeping or WorkflowStatus.WaitingForEvent => run.ResumeAt is { } resumeAt && resumeAt <= now,
        WorkflowStatus.Pending or WorkflowStatus.Running => run.LeaseExpiresAt is not { } lease || lease <= now,
        _ => false,
    };

    /// <summary>Desde cuándo está vencida (para atender primero las más atrasadas).</summary>
    public static DateTimeOffset DueAt(WorkflowRun run) =>
        run.Status is WorkflowStatus.Sleeping or WorkflowStatus.WaitingForEvent
            ? run.ResumeAt ?? run.UpdatedAt
            : run.LeaseExpiresAt ?? run.UpdatedAt;

    /// <summary>La ejecución espera <paramref name="eventName"/> sin clave o con la clave <paramref name="key"/>.</summary>
    public static bool IsWaitingFor(WorkflowRun run, string eventName, string? key) =>
        run.Status == WorkflowStatus.WaitingForEvent
        && run.Waiting is { } wait
        && wait.EventName == eventName
        && (wait.Key is null || wait.Key == key);

    /// <summary>Activa y con <see cref="Automation.NextRunAt"/> &lt;= <paramref name="now"/>.</summary>
    public static bool IsDue(Automation automation, DateTimeOffset now) => automation.Enabled && automation.NextRunAt is { } next && next <= now;

    /// <summary>Cuándo terminó (para la retención).</summary>
    public static DateTimeOffset FinishedAt(WorkflowRun run) => run.CompletedAt ?? run.UpdatedAt;

    internal static WorkflowConcurrencyException Conflict(string kind, string id, long expected, long stored) =>
        new($"La {kind} '{id}' cambió en el almacén (versión {stored}, se esperaba {expected}).");

    /// <summary>Copia ligera para índices: sin pasos, registro, entrada ni salida.</summary>
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
/// Almacén en memoria (pruebas y desarrollo; se pierde al reiniciar). Conserva como mucho
/// <see cref="MaxFinishedRuns"/> ejecuciones terminadas: al superarlo descarta las más antiguas.
/// </summary>
public sealed class InMemoryWorkflowStore(int maxFinishedRuns = 10_000) : IWorkflowStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Automation> _automations = new(StringComparer.Ordinal);
    private int _finished;

    /// <summary>Ejecuciones terminadas que se conservan como máximo.</summary>
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
/// Almacén en disco (un JSON por ejecución y por automatización). Sobrevive reinicios. Las ejecuciones activas tienen
/// además una entrada pequeña en <c>runs-active/</c>, de modo que los ciclos del motor no leen el historial completo.
/// </summary>
/// <remarks>
/// Varios procesos de la <b>misma máquina</b> pueden compartir el directorio: las escrituras se serializan con un archivo
/// de bloqueo (<c>.lock</c>) y se verifican las versiones. Para varias máquinas use un almacén de base de datos, como
/// <c>PostgresWorkflowStore</c> (paquete <c>AiSdk.Net.Workflows.Postgres</c>): el bloqueo de archivos no es fiable en
/// discos de red.
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

    /// <summary>Crea el índice de ejecuciones activas la primera vez (almacenes creados con versiones anteriores).</summary>
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

    /// <summary>Filtra con el índice de activas y confirma cada candidata con la ejecución guardada.</summary>
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

    /// <summary>Serializa las escrituras dentro del proceso (semáforo) y entre procesos (archivo de bloqueo).</summary>
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
