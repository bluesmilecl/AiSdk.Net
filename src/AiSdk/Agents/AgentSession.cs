using System.Collections.Concurrent;

namespace AiSdk;

/// <summary>Conversation store (agent memory). Implement your own for EF Core, Redis, etc.</summary>
public interface IConversationStore
{
    Task<IReadOnlyList<ModelMessage>> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    Task AppendAsync(string conversationId, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default);

    Task ClearAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>In-memory store (per process).</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, List<ModelMessage>> _conversations = new();

    public Task<IReadOnlyList<ModelMessage>> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var list = _conversations.GetOrAdd(conversationId, _ => []);
        lock (list)
        {
            return Task.FromResult<IReadOnlyList<ModelMessage>>(list.ToList());
        }
    }

    public Task AppendAsync(string conversationId, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = _conversations.GetOrAdd(conversationId, _ => []);
        lock (list)
        {
            list.AddRange(messages);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _conversations.TryRemove(conversationId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Conversation with memory: loads the history, calls the agent and saves the
/// new messages (equivalent to Laravel AI SDK's <c>RemembersConversations</c>).
/// </summary>
/// <remarks>
/// The history is never left with tool calls lacking a result, which would make subsequent requests fail:
/// if the conversation continues with a new message while calls are still pending (client tools
/// or unanswered deferred approvals), they are closed with an error result that is also saved.
/// </remarks>
public sealed class AgentSession
{
    private const string AbandonedCallMessage = "La llamada no se completó: la conversación continuó sin su resultado.";

    private readonly Agent _agent;
    private readonly IConversationStore _store;

    internal AgentSession(Agent agent, string conversationId, IConversationStore store)
    {
        _agent = agent;
        _store = store;
        ConversationId = conversationId;
    }

    public string ConversationId { get; }

    public Task<IReadOnlyList<ModelMessage>> GetMessagesAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(ConversationId, cancellationToken);

    /// <summary>
    /// Gets the calls without a result at the end of the conversation: deferred approvals
    /// (<see cref="CallSettings.DeferToolApprovals"/>) or client tools.
    /// </summary>
    public async Task<IReadOnlyList<ToolCallPart>> GetPendingToolCallsAsync(CancellationToken cancellationToken = default)
    {
        var history = await _store.LoadAsync(ConversationId, cancellationToken).ConfigureAwait(false);
        var index = history.ToList().FindLastIndex(m => m.Role != ChatRole.Tool);
        if (index < 0 || history[index].Role != ChatRole.Assistant)
        {
            return [];
        }

        var answered = history.Skip(index + 1).SelectMany(m => m.Content.OfType<ToolResultPart>()).Select(r => r.ToolCallId).ToHashSet(StringComparer.Ordinal);
        return history[index].Content.OfType<ToolCallPart>().Where(c => !c.ProviderExecuted && !answered.Contains(c.ToolCallId)).ToList();
    }

    public async Task<GenerateTextResult> SendAsync(string message, CancellationToken cancellationToken = default) =>
        await SendAsync(ModelMessage.User(message), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Sends a message. To answer pending client tools, send a
    /// <see cref="ChatRole.Tool"/> message with their results.
    /// </summary>
    public async Task<GenerateTextResult> SendAsync(ModelMessage message, CancellationToken cancellationToken = default)
    {
        var options = await BuildCallAsync(message, null, cancellationToken).ConfigureAwait(false);
        return await _agent.GenerateAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a message with streaming. Messages are saved as each step finishes, so
    /// the history is complete when the stream ends.
    /// </summary>
    public async Task<StreamTextResult> StreamAsync(string message, CancellationToken cancellationToken = default)
    {
        var options = await BuildCallAsync(ModelMessage.User(message), null, cancellationToken).ConfigureAwait(false);
        return _agent.Stream(options);
    }

    /// <summary>
    /// Resumes a conversation stopped by deferred approvals (<see cref="CallSettings.DeferToolApprovals"/>):
    /// runs the approved calls, answers denied or undecided calls with an error, and continues.
    /// </summary>
    /// <exception cref="InvalidOperationException">The conversation has no pending calls.</exception>
    public async Task<GenerateTextResult> SendApprovalsAsync(IReadOnlyList<ToolApprovalResponse> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        var options = await BuildCallAsync(null, approvals, cancellationToken).ConfigureAwait(false);
        return await _agent.GenerateAsync(options).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="SendApprovalsAsync"/>, with streaming.</summary>
    /// <exception cref="InvalidOperationException">The conversation has no pending calls.</exception>
    public async Task<StreamTextResult> StreamApprovalsAsync(IReadOnlyList<ToolApprovalResponse> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        var options = await BuildCallAsync(null, approvals, cancellationToken).ConfigureAwait(false);
        return _agent.Stream(options);
    }

    private async Task<AgentCallOptions> BuildCallAsync(ModelMessage? message, IReadOnlyList<ToolApprovalResponse>? approvals, CancellationToken cancellationToken)
    {
        if (approvals is not null && (await GetPendingToolCallsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            throw new InvalidOperationException($"La conversación '{ConversationId}' no tiene llamadas pendientes de aprobación.");
        }

        var stored = await _store.LoadAsync(ConversationId, cancellationToken).ConfigureAwait(false);

        // Al reanudar aprobaciones, las llamadas pendientes del final las resuelve el motor; con un mensaje nuevo se
        // cierran con un error (y ese cierre se guarda) para que el historial siga siendo válido para el proveedor.
        var (history, closure) = CloseDanglingToolCalls(stored, message, closeTrailing: approvals is null);
        IReadOnlyList<ModelMessage> leading = [.. closure is null ? [] : new[] { closure }, .. message is null ? [] : new[] { message }];
        var first = true;
        return new AgentCallOptions
        {
            Messages = [.. history, .. leading],
            ToolApprovals = approvals,
            CancellationToken = cancellationToken,
            ConversationId = ConversationId,
            OnStepFinish = async step =>
            {
                IReadOnlyList<ModelMessage> toSave = first ? [.. leading, .. step.ResponseMessages] : step.ResponseMessages;
                first = false;
                await _store.AppendAsync(ConversationId, toSave, CancellationToken.None).ConfigureAwait(false);
            },
        };
    }

    /// <summary>
    /// Closes tool calls that have no result with an error result. Intermediate ones (e.g. saved
    /// by earlier versions) are closed in memory only; the closure of trailing ones is returned separately so it can be
    /// saved. <paramref name="incoming"/> may carry results (a <see cref="ChatRole.Tool"/> message).
    /// </summary>
    internal static (List<ModelMessage> History, ModelMessage? Closure) CloseDanglingToolCalls(
        IReadOnlyList<ModelMessage> history,
        ModelMessage? incoming,
        bool closeTrailing)
    {
        var result = new List<ModelMessage>(history.Count + 1);
        ModelMessage? closure = null;
        for (var i = 0; i < history.Count; i++)
        {
            result.Add(history[i]);
            var calls = history[i].Role == ChatRole.Assistant
                ? history[i].Content.OfType<ToolCallPart>().Where(c => !c.ProviderExecuted).ToList()
                : [];
            if (calls.Count == 0)
            {
                continue;
            }

            var answered = new HashSet<string>(StringComparer.Ordinal);
            var next = i + 1;
            for (; next < history.Count && history[next].Role == ChatRole.Tool; next++)
            {
                answered.UnionWith(history[next].Content.OfType<ToolResultPart>().Select(r => r.ToolCallId));
                result.Add(history[next]);
            }

            var trailing = next == history.Count;
            if (trailing && incoming?.Role == ChatRole.Tool)
            {
                answered.UnionWith(incoming.Content.OfType<ToolResultPart>().Select(r => r.ToolCallId));
            }

            i = next - 1;
            var missing = calls.Where(c => !answered.Contains(c.ToolCallId)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            var close = ModelMessage.Tool(missing.Select(c => new ToolResultPart(c.ToolCallId, c.ToolName, Tool.ErrorOutput(AbandonedCallMessage)) { IsError = true }).ToArray());
            if (!trailing)
            {
                result.Add(close);
            }
            else if (closeTrailing)
            {
                closure = close;
            }
        }

        return (result, closure);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => _store.ClearAsync(ConversationId, cancellationToken);
}
