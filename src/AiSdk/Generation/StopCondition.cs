namespace AiSdk;

/// <summary>
/// Condición de parada del bucle de herramientas (equivalente a <c>stopWhen</c>).
/// Se combinan con <c>|</c> (cualquiera) o <c>&amp;</c> (todas).
/// </summary>
public sealed class StopCondition
{
    private readonly Func<IReadOnlyList<StepResult>, bool> _predicate;

    public StopCondition(Func<IReadOnlyList<StepResult>, bool> predicate) => _predicate = predicate;

    public bool IsMet(IReadOnlyList<StepResult> steps) => _predicate(steps);

    /// <summary>Se detiene al alcanzar <paramref name="count"/> pasos.</summary>
    public static StopCondition StepCountIs(int count) => new(steps => steps.Count >= count);

    /// <summary>Se detiene cuando el último paso llamó a la herramienta indicada.</summary>
    public static StopCondition HasToolCall(string toolName) =>
        new(steps => steps.Count > 0 && steps[^1].ToolCalls.Any(c => c.ToolName == toolName));

    /// <summary>Se detiene cuando el uso acumulado de tokens supera el límite.</summary>
    public static StopCondition TotalTokensExceed(int maxTokens) =>
        new(steps => steps.Sum(s => s.Usage.TotalTokens ?? 0) >= maxTokens);

    public static StopCondition operator |(StopCondition a, StopCondition b) => new(s => a.IsMet(s) || b.IsMet(s));

    public static StopCondition operator &(StopCondition a, StopCondition b) => new(s => a.IsMet(s) && b.IsMet(s));

    public static implicit operator StopCondition(Func<IReadOnlyList<StepResult>, bool> predicate) => new(predicate);
}
