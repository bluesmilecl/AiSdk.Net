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
public sealed class AgentSession
{
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

    public async Task<GenerateTextResult> SendAsync(string message, CancellationToken cancellationToken = default) =>
        await SendAsync(ModelMessage.User(message), cancellationToken).ConfigureAwait(false);

    public Task<GenerateTextResult> SendAsync(ModelMessage message, CancellationToken cancellationToken = default) =>
        SendCoreAsync(message, cancellationToken);

    /// <summary>
    /// Envía en streaming. Los mensajes se guardan al terminar cada paso, por lo que
    /// el historial está completo cuando el stream finaliza.
    /// </summary>
    public async Task<StreamTextResult> StreamAsync(string message, CancellationToken cancellationToken = default)
    {
        var (options, _) = await BuildCallAsync(ModelMessage.User(message), cancellationToken).ConfigureAwait(false);
        return _agent.Stream(options);
    }

    private async Task<GenerateTextResult> SendCoreAsync(ModelMessage message, CancellationToken cancellationToken)
    {
        var (options, _) = await BuildCallAsync(message, cancellationToken).ConfigureAwait(false);
        return await _agent.GenerateAsync(options).ConfigureAwait(false);
    }

    private async Task<(AgentCallOptions, IReadOnlyList<ModelMessage>)> BuildCallAsync(ModelMessage message, CancellationToken cancellationToken)
    {
        var history = await _store.LoadAsync(ConversationId, cancellationToken).ConfigureAwait(false);
        var first = true;
        var options = new AgentCallOptions
        {
            Messages = [.. history, message],
            CancellationToken = cancellationToken,
            OnStepFinish = async step =>
            {
                IReadOnlyList<ModelMessage> toSave = first ? [message, .. step.ResponseMessages] : step.ResponseMessages;
                first = false;
                await _store.AppendAsync(ConversationId, toSave, CancellationToken.None).ConfigureAwait(false);
            },
        };
        return (options, history);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => _store.ClearAsync(ConversationId, cancellationToken);
}
