using System.Text.Json;
using System.Text.Json.Serialization;
using AiSdk.Workflows.Resources;

namespace AiSdk.Workflows;

/// <summary>Status of a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowStatus>))]
public enum WorkflowStatus
{
    Pending,
    Running,

    /// <summary>Sleeping until <see cref="WorkflowRun.ResumeAt"/>.</summary>
    Sleeping,

    /// <summary>Waiting for an external event (approval, webhook, a user's reply...).</summary>
    WaitingForEvent,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Memoized result of a step.</summary>
public sealed record StepRecord(string Name, JsonElement? Output = null, int Attempts = 1, DateTimeOffset CompletedAt = default)
{
    /// <summary>For event waits: true if the timeout elapsed without the event being received.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>Event a run is waiting for.</summary>
public sealed record EventWait(string EventName, string? Key = null, string StepKey = "", DateTimeOffset? TimeoutAt = null);

/// <summary>
/// A run of a workflow. It is persisted after each step: if the process restarts, the run continues from the last
/// completed step.
/// </summary>
public sealed class WorkflowRun
{
    public required string Id { get; init; }

    public required string WorkflowName { get; init; }

    public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;

    public JsonElement Input { get; init; }

    public JsonElement? Output { get; set; }

    public string? Error { get; set; }

    /// <summary>How many times the workflow was executed (or resumed).</summary>
    public int Executions { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When to resume (sleep or expiration of a wait).</summary>
    public DateTimeOffset? ResumeAt { get; set; }

    public EventWait? Waiting { get; set; }

    /// <summary>Origin: <c>manual</c>, <c>cron:{id}</c>, <c>event:{name}</c>, <c>webhook:{id}</c>...</summary>
    public string? TriggeredBy { get; init; }

    public Dictionary<string, StepRecord> Steps { get; init; } = [];

    public List<string> Logs { get; init; } = [];

    /// <summary>
    /// Version for optimistic concurrency: 0 if not saved yet. <see cref="IWorkflowStore.SaveRunAsync"/> only saves if it
    /// matches the stored version, and increments it; otherwise, it throws <see cref="WorkflowConcurrencyException"/>.
    /// </summary>
    public long Version { get; set; }

    /// <summary>Engine instance (<see cref="WorkflowEngineOptions.InstanceId"/>) that holds the claim on the run.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// Until when the claim of <see cref="LeaseOwner"/> is valid. Until it expires, other instances do not run it; if the
    /// instance dies, another one takes it over once it expires.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public bool IsFinished => Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled;

    /// <summary>Deserializes the output.</summary>
    public T? GetOutput<T>() => Output is { } o ? o.Deserialize<T>(JsonSchema.DefaultSerializerOptions) : default;

    internal WorkflowRun Clone() => JsonSerializer.Deserialize<WorkflowRun>(JsonSerializer.Serialize(this, JsonSchema.DefaultSerializerOptions), JsonSchema.DefaultSerializerOptions)!;
}

/// <summary>Definition of a workflow that can be registered in the <see cref="WorkflowEngine"/>.</summary>
public interface IWorkflow
{
    string Name { get; }

    string? Description { get; }

    Task<object?> ExecuteAsync(WorkflowContext context, JsonElement input);
}

/// <summary>
/// Typed workflow defined as code (in the style of Temporal, Inngest or Vercel Workflow).
/// The code must be deterministic: every side effect (calls to models, APIs, emails...)
/// goes inside <see cref="WorkflowContext.StepAsync{T}(string, Func{CancellationToken, Task{T}}, StepOptions?)"/>
/// so that it is not repeated when the run resumes.
/// </summary>
public sealed class Workflow<TInput, TOutput>(string name, Func<WorkflowContext, TInput, Task<TOutput>> run, string? description = null) : IWorkflow
{
    public string Name => name;

    public string? Description => description;

    public async Task<object?> ExecuteAsync(WorkflowContext context, JsonElement input)
    {
        var typed = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default!
            : input.Deserialize<TInput>(JsonSchema.DefaultSerializerOptions)!;
        return await run(context, typed).ConfigureAwait(false);
    }
}

/// <summary>Workflow factory.</summary>
public static class Workflow
{
    public static Workflow<TInput, TOutput> Create<TInput, TOutput>(string name, Func<WorkflowContext, TInput, Task<TOutput>> run, string? description = null) =>
        new(name, run, description);
}

/// <summary>Retry policy of a step.</summary>
public sealed record StepOptions
{
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffFactor { get; init; } = 2;

    /// <summary>Maximum time per attempt.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Decides whether an exception is retried (by default all except <see cref="NonRetryableException"/>).</summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    public static StepOptions Default { get; } = new();

    public static StepOptions NoRetry { get; } = new() { MaxAttempts = 1 };
}

/// <summary>
/// The run or automation changed in the store since it was read (another replica or process modified it, e.g. when
/// cancelling it). Whoever receives this exception must read it again instead of overwriting it.
/// </summary>
public sealed class WorkflowConcurrencyException(string message) : InvalidOperationException(message);

/// <summary>Error that must not be retried (invalid data, permissions...).</summary>
public sealed class NonRetryableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Internal signal: the run is suspended (sleep or event wait) and will be resumed later.</summary>
public sealed class WorkflowSuspendedException : Exception
{
    internal WorkflowSuspendedException(WorkflowStatus status, DateTimeOffset? resumeAt)
        : base(Strings.Workflow_Suspended)
    {
        Status = status;
        ResumeAt = resumeAt;
    }

    public WorkflowStatus Status { get; }

    public DateTimeOffset? ResumeAt { get; }
}
