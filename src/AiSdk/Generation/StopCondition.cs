namespace AiSdk;

/// <summary>
/// Stop condition of the tool loop (equivalent to <c>stopWhen</c>).
/// Conditions are combined with <c>|</c> (any) or <c>&amp;</c> (all).
/// </summary>
public sealed class StopCondition
{
    private readonly Func<IReadOnlyList<StepResult>, bool> _predicate;

    public StopCondition(Func<IReadOnlyList<StepResult>, bool> predicate) => _predicate = predicate;

    public bool IsMet(IReadOnlyList<StepResult> steps) => _predicate(steps);

    /// <summary>Stops when <paramref name="count"/> steps have been reached.</summary>
    public static StopCondition StepCountIs(int count) => new(steps => steps.Count >= count);

    /// <summary>Stops when the last step called the specified tool.</summary>
    public static StopCondition HasToolCall(string toolName) =>
        new(steps => steps.Count > 0 && steps[^1].ToolCalls.Any(c => c.ToolName == toolName));

    /// <summary>Stops when the accumulated token usage exceeds the limit.</summary>
    public static StopCondition TotalTokensExceed(int maxTokens) =>
        new(steps => steps.Sum(s => s.Usage.TotalTokens ?? 0) >= maxTokens);

    public static StopCondition operator |(StopCondition a, StopCondition b) => new(s => a.IsMet(s) || b.IsMet(s));

    public static StopCondition operator &(StopCondition a, StopCondition b) => new(s => a.IsMet(s) && b.IsMet(s));

    public static implicit operator StopCondition(Func<IReadOnlyList<StepResult>, bool> predicate) => new(predicate);
}
