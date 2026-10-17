using System.Collections.Concurrent;
using System.Text.Json;

namespace AiSdk.Workflows;

/// <summary>Persistencia de ejecuciones y automatizaciones. Implemente esta interfaz para SQL, Redis, Cosmos...</summary>
public interface IWorkflowStore
{
    Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default);

    Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Ejecuciones filtradas (más recientes primero).</summary>
    Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default);

    Task SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default);

    Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default);

    Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Almacén en memoria (pruebas y desarrollo; se pierde al reiniciar).</summary>
public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly ConcurrentDictionary<string, string> _runs = new();
    private readonly ConcurrentDictionary<string, string> _automations = new();

    public Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        _runs[run.Id] = JsonSerializer.Serialize(run, JsonSchema.DefaultSerializerOptions);
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_runs.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<WorkflowRun>(json, JsonSchema.DefaultSerializerOptions) : null);

    public Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowRun>>(_runs.Values
            .Select(j => JsonSerializer.Deserialize<WorkflowRun>(j, JsonSchema.DefaultSerializerOptions)!)
            .Where(r => (status is null || r.Status == status) && (workflowName is null || r.WorkflowName == workflowName))
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToList());

    public Task SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default)
    {
        _automations[automation.Id] = JsonSerializer.Serialize(automation, JsonSchema.DefaultSerializerOptions);
        return Task.CompletedTask;
    }

    public Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_automations.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<Automation>(json, JsonSchema.DefaultSerializerOptions) : null);

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Automation>>(_automations.Values.Select(j => JsonSerializer.Deserialize<Automation>(j, JsonSchema.DefaultSerializerOptions)!).OrderBy(a => a.CreatedAt).ToList());

    public Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        _automations.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Almacén en disco (un JSON por ejecución y por automatización). Sobrevive reinicios.</summary>
public sealed class FileSystemWorkflowStore : IWorkflowStore, IDisposable
{
    private readonly string _runs;
    private readonly string _automations;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileSystemWorkflowStore(string directory)
    {
        _runs = Directory.CreateDirectory(Path.Combine(directory, "runs")).FullName;
        _automations = Directory.CreateDirectory(Path.Combine(directory, "automations")).FullName;
    }

    public void Dispose() => _lock.Dispose();

    private static string SafeName(string id) =>
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.Contains("..", StringComparison.Ordinal)
            ? id + ".json"
            : throw new ArgumentException($"Id inválido: '{id}'.");

    public Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default) =>
        WriteAsync(Path.Combine(_runs, SafeName(run.Id)), run, cancellationToken);

    public Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default) =>
        ReadAsync<WorkflowRun>(Path.Combine(_runs, SafeName(id)), cancellationToken);

    public async Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var runs = new List<WorkflowRun>();
        foreach (var file in Directory.GetFiles(_runs, "*.json"))
        {
            if (await ReadAsync<WorkflowRun>(file, cancellationToken).ConfigureAwait(false) is { } run
                && (status is null || run.Status == status) && (workflowName is null || run.WorkflowName == workflowName))
            {
                runs.Add(run);
            }
        }

        return runs.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public Task SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default) =>
        WriteAsync(Path.Combine(_automations, SafeName(automation.Id)), automation, cancellationToken);

    public Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default) =>
        ReadAsync<Automation>(Path.Combine(_automations, SafeName(id)), cancellationToken);

    public async Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Automation>();
        foreach (var file in Directory.GetFiles(_automations, "*.json"))
        {
            if (await ReadAsync<Automation>(file, cancellationToken).ConfigureAwait(false) is { } automation)
            {
                list.Add(automation);
            }
        }

        return list.OrderBy(a => a.CreatedAt).ToList();
    }

    public Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        File.Delete(Path.Combine(_automations, SafeName(id)));
        return Task.CompletedTask;
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Escritura atómica: archivo temporal + reemplazo.
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, JsonSchema.DefaultSerializerOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonSchema.DefaultSerializerOptions);
    }
}
