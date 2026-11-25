using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiSdk.Internal;

namespace AiSdk.UI;

/// <summary>
/// Writer for composing a custom UI stream (equivalent to <c>createUIMessageStream</c>):
/// merges one or more <see cref="StreamTextResult"/> with custom data (<c>data-*</c>).
/// </summary>
public sealed class UIMessageStreamWriter
{
    private readonly Channel<JsonObject> _channel = Channel.CreateUnbounded<JsonObject>();
    private readonly ConcurrentBag<Task> _merges = [];
    private readonly Func<Exception, string>? _onError;

    internal UIMessageStreamWriter(Func<Exception, string>? onError) => _onError = onError;

    internal ChannelReader<JsonObject> Reader => _channel.Reader;

    /// <summary>Writes a raw protocol chunk.</summary>
    public void Write(JsonObject chunk) => _channel.Writer.TryWrite(chunk);

    /// <summary>
    /// Sends custom data as a <c>data-{type}</c> part. With the same <paramref name="id"/>, the client
    /// replaces the previous data (useful for progress). Transient data is not saved in the message.
    /// </summary>
    public void WriteData(string type, object? data, string? id = null, bool transient = false) =>
        Write(UIMessageStream.DataChunk(
            type,
            data is null ? null : JsonSerializer.SerializeToNode(data, data.GetType(), JsonSchema.DefaultSerializerOptions),
            id,
            transient));

    /// <summary>Sends a source (<c>source-url</c>).</summary>
    public void WriteSource(string url, string? title = null)
    {
        // Sin título no se envía "title": null (ai 6.0.x valida el chunk con title opcional de tipo string y rechaza null).
        var chunk = new JsonObject { ["type"] = "source-url", ["sourceId"] = Guid.NewGuid().ToString("N"), ["url"] = url };
        if (title is not null)
        {
            chunk["title"] = title;
        }

        Write(chunk);
    }

    /// <summary>
    /// Merges a text stream into this stream (without its <c>start</c>/<c>finish</c> chunks, which
    /// the writer emits). The final stream does not end until all merges have finished.
    /// <paramref name="toolApprovals"/> emits the pending approvals (see <see cref="UIMessageStream.ToChunks"/>).
    /// </summary>
    public void Merge(StreamTextResult result, ToolApprovalProtector? toolApprovals = null) => Merge(result.FullStream, toolApprovals);

    /// <inheritdoc cref="Merge(StreamTextResult, ToolApprovalProtector?)"/>
    public void Merge(IAsyncEnumerable<StreamPart> parts, ToolApprovalProtector? toolApprovals = null)
    {
        _merges.Add(Task.Run(async () =>
        {
            await foreach (var chunk in UIMessageStream.ToChunks(parts, onError: _onError, toolApprovals: toolApprovals).ConfigureAwait(false))
            {
                var type = (string?)chunk["type"];
                if (type is not ("start" or "finish"))
                {
                    Write(chunk);
                }
            }
        }));
    }

    internal async Task CompleteAsync(Task execute)
    {
        try
        {
            await execute.ConfigureAwait(false);
            while (true)
            {
                var pending = _merges.ToArray();
                await Task.WhenAll(pending).ConfigureAwait(false);
                if (_merges.Count == pending.Length)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Write(new JsonObject { ["type"] = "error", ["errorText"] = UIMessageStream.ErrorText(ex, _onError) });
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}

public static partial class UIMessageStream
{
    /// <summary>
    /// Creates a custom UI stream. Example:
    /// <code>
    /// var chunks = UIMessageStream.Create(async writer =>
    /// {
    ///     writer.WriteData("status", new { step = "searching" }, id: "status");
    ///     writer.Merge(agent.Stream(messages));
    /// });
    /// </code>
    /// </summary>
    public static async IAsyncEnumerable<JsonObject> Create(
        Func<UIMessageStreamWriter, Task> execute,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var writer = new UIMessageStreamWriter(onError);
        yield return messageId is null ? new JsonObject { ["type"] = "start" } : new JsonObject { ["type"] = "start", ["messageId"] = messageId };

        var completion = writer.CompleteAsync(Task.Run(() => execute(writer), cancellationToken));
        await foreach (var chunk in writer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }

        await completion.ConfigureAwait(false);
        yield return new JsonObject { ["type"] = "finish" };
    }

    /// <summary>Converts protocol chunks into SSE events (<c>data: {...}\n\n</c>), ending with <c>[DONE]</c>.</summary>
    public static async IAsyncEnumerable<string> ToSseEvents(IAsyncEnumerable<JsonObject> chunks, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return "data: " + chunk.ToJsonString() + "\n\n";
        }

        yield return "data: [DONE]\n\n";
    }

    /// <summary>Writes protocol chunks as SSE to <paramref name="output"/>.</summary>
    public static async Task WriteChunksAsync(IAsyncEnumerable<JsonObject> chunks, Stream output, CancellationToken cancellationToken = default)
    {
        await foreach (var sse in ToSseEvents(chunks, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(sse), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

// ---------------------------------------------------------------------------
// Streams reanudables
// ---------------------------------------------------------------------------

/// <summary>
/// Store of resumable streams: saves the SSE events of each response so that the client
/// can reconnect (page reload, network change) and continue where it left off.
/// Implement your own on top of Redis for multiple server instances.
/// </summary>
public interface IResumableStreamStore
{
    Task CreateAsync(string streamId, CancellationToken cancellationToken = default);

    Task AppendAsync(string streamId, string sseEvent, CancellationToken cancellationToken = default);

    Task CompleteAsync(string streamId, CancellationToken cancellationToken = default);

    /// <summary>Returns the events from the beginning and follows new ones until the stream ends.</summary>
    IAsyncEnumerable<string> ReadAsync(string streamId, CancellationToken cancellationToken = default);

    Task SetActiveStreamAsync(string chatId, string? streamId, CancellationToken cancellationToken = default);

    Task<string?> GetActiveStreamAsync(string chatId, CancellationToken cancellationToken = default);
}

/// <summary>In-memory store (a single server instance). Streams expire after <paramref name="ttl"/>.</summary>
public sealed class InMemoryResumableStreamStore(TimeSpan? ttl = null) : IResumableStreamStore
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (ReplayBuffer<string> Buffer, DateTimeOffset Created)> _streams = new();
    private readonly ConcurrentDictionary<string, string> _active = new();

    public Task CreateAsync(string streamId, CancellationToken cancellationToken = default)
    {
        Cleanup();
        _streams[streamId] = (new ReplayBuffer<string>(), DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task AppendAsync(string streamId, string sseEvent, CancellationToken cancellationToken = default)
    {
        if (_streams.TryGetValue(streamId, out var entry))
        {
            entry.Buffer.Add(sseEvent);
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(string streamId, CancellationToken cancellationToken = default)
    {
        if (_streams.TryGetValue(streamId, out var entry))
        {
            entry.Buffer.Complete();
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadAsync(string streamId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(streamId, out var entry))
        {
            yield break;
        }

        await foreach (var item in entry.Buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task SetActiveStreamAsync(string chatId, string? streamId, CancellationToken cancellationToken = default)
    {
        if (streamId is null)
        {
            _active.TryRemove(chatId, out _);
        }
        else
        {
            _active[chatId] = streamId;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetActiveStreamAsync(string chatId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_active.TryGetValue(chatId, out var id) && _streams.ContainsKey(id) ? id : null);

    private void Cleanup()
    {
        var limit = DateTimeOffset.UtcNow - _ttl;
        foreach (var (id, entry) in _streams)
        {
            if (entry.Created < limit)
            {
                _streams.TryRemove(id, out _);
            }
        }
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Nombre del protocolo UI Message Stream del AI SDK; es una clase estática de utilidades, no un Stream.")]
public static class ResumableUIMessageStream
{
    /// <summary>
    /// Key of the chat within its owner's scope. The chat id is chosen by the client, so on its own it cannot be used
    /// for authorization: by combining it with the user (or a server-issued session), nobody can replace or
    /// resume another user's stream even if they know or guess their chat id.
    /// </summary>
    public static string ScopedChatId(string owner, string chatId)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        return $"{owner.Length}:{owner}:{chatId}";
    }

    /// <summary>
    /// Starts writing the stream to the store in the background (it continues even if the client
    /// disconnects) and marks it as the chat's active stream. Returns the stream id.
    /// <paramref name="chatId"/> is the store key: on a server with multiple users use
    /// <see cref="ScopedChatId"/> (the <c>AiSdk.AspNetCore</c> endpoints already do).
    /// </summary>
    public static async Task<string> StartAsync(IResumableStreamStore store, string chatId, IAsyncEnumerable<JsonObject> chunks)
    {
        var streamId = Guid.NewGuid().ToString("N");
        await store.CreateAsync(streamId).ConfigureAwait(false);
        await store.SetActiveStreamAsync(chatId, streamId).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var sse in UIMessageStream.ToSseEvents(chunks).ConfigureAwait(false))
                {
                    await store.AppendAsync(streamId, sse).ConfigureAwait(false);
                }
            }
            finally
            {
                // Primero se libera el chat y luego se completa: quien vea el fin del stream ya no lo encuentra activo.
                if (await store.GetActiveStreamAsync(chatId).ConfigureAwait(false) == streamId)
                {
                    await store.SetActiveStreamAsync(chatId, null).ConfigureAwait(false);
                }

                await store.CompleteAsync(streamId).ConfigureAwait(false);
            }
        });
        return streamId;
    }
}
