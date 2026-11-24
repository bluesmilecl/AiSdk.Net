using System.Collections.Concurrent;

namespace AiSdk;

/// <summary>Almacén de conversaciones (memoria del agente). Implementa uno propio para EF Core, Redis, etc.</summary>
public interface IConversationStore
{
    Task<IReadOnlyList<ModelMessage>> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    Task AppendAsync(string conversationId, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default);

    Task ClearAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>Almacén en memoria (por proceso).</summary>
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
/// Conversación con memoria: carga el historial, llama al agente y guarda los
/// mensajes nuevos (equivalente a <c>RemembersConversations</c> de Laravel AI SDK).
/// </summary>
/// <remarks>
/// El historial nunca queda con llamadas a herramientas sin resultado que hagan fallar las peticiones siguientes:
/// si la conversación continúa con un mensaje nuevo mientras quedan llamadas pendientes (herramientas de cliente
/// o aprobaciones diferidas sin responder), se cierran con un resultado de error que también se guarda.
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
    /// Llamadas sin resultado al final de la conversación: aprobaciones diferidas
    /// (<see cref="CallSettings.DeferToolApprovals"/>) o herramientas de cliente.
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
    /// Envía un mensaje. Para responder herramientas de cliente pendientes, envíe un mensaje
    /// <see cref="ChatRole.Tool"/> con sus resultados.
    /// </summary>
    public async Task<GenerateTextResult> SendAsync(ModelMessage message, CancellationToken cancellationToken = default)
    {
        var options = await BuildCallAsync(message, null, cancellationToken).ConfigureAwait(false);
        return await _agent.GenerateAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Envía en streaming. Los mensajes se guardan al terminar cada paso, por lo que
    /// el historial está completo cuando el stream finaliza.
    /// </summary>
    public async Task<StreamTextResult> StreamAsync(string message, CancellationToken cancellationToken = default)
    {
        var options = await BuildCallAsync(ModelMessage.User(message), null, cancellationToken).ConfigureAwait(false);
        return _agent.Stream(options);
    }

    /// <summary>
    /// Reanuda una conversación detenida por aprobaciones diferidas (<see cref="CallSettings.DeferToolApprovals"/>):
    /// ejecuta las llamadas aprobadas, responde con un error las denegadas o sin decisión, y continúa.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si la conversación no tiene llamadas pendientes.</exception>
    public async Task<GenerateTextResult> SendApprovalsAsync(IReadOnlyList<ToolApprovalResponse> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        var options = await BuildCallAsync(null, approvals, cancellationToken).ConfigureAwait(false);
        return await _agent.GenerateAsync(options).ConfigureAwait(false);
    }

    /// <summary>Como <see cref="SendApprovalsAsync"/>, en streaming.</summary>
    /// <exception cref="InvalidOperationException">Si la conversación no tiene llamadas pendientes.</exception>
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
            OnStepFinish = async step =>
            {
                IReadOnlyList<ModelMessage> toSave = first ? [.. leading, .. step.ResponseMessages] : step.ResponseMessages;
                first = false;
                await _store.AppendAsync(ConversationId, toSave, CancellationToken.None).ConfigureAwait(false);
            },
        };
    }

    /// <summary>
    /// Cierra con un resultado de error las llamadas a herramientas sin resultado. Las intermedias (p. ej. guardadas
    /// por versiones anteriores) se cierran solo en memoria; el cierre de las del final se devuelve aparte para
    /// guardarlo. <paramref name="incoming"/> puede traer resultados (mensaje <see cref="ChatRole.Tool"/>).
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
