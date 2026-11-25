using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiSdk.Internal;

namespace AiSdk.UI;

/// <summary>
/// Escritor para componer un stream UI propio (equivalente a <c>createUIMessageStream</c>):
/// mezcla uno o varios <see cref="StreamTextResult"/> con datos propios (<c>data-*</c>).
/// </summary>
public sealed class UIMessageStreamWriter
{
    private readonly Channel<JsonObject> _channel = Channel.CreateUnbounded<JsonObject>();
    private readonly ConcurrentBag<Task> _merges = [];
    private readonly Func<Exception, string>? _onError;

    internal UIMessageStreamWriter(Func<Exception, string>? onError) => _onError = onError;

    internal ChannelReader<JsonObject> Reader => _channel.Reader;

    /// <summary>Escribe un chunk crudo del protocolo.</summary>
    public void Write(JsonObject chunk) => _channel.Writer.TryWrite(chunk);

    /// <summary>
    /// Envía datos propios como parte <c>data-{type}</c>. Con el mismo <paramref name="id"/>, el cliente
    /// reemplaza el dato anterior (útil para progreso). Los transitorios no se guardan en el mensaje.
    /// </summary>
    public void WriteData(string type, object? data, string? id = null, bool transient = false) =>
        Write(UIMessageStream.DataChunk(
            type,
            data is null ? null : JsonSerializer.SerializeToNode(data, data.GetType(), JsonSchema.DefaultSerializerOptions),
            id,
            transient));

    /// <summary>Envía una fuente (<c>source-url</c>).</summary>
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
    /// Mezcla un stream de texto en este stream (sin sus chunks <c>start</c>/<c>finish</c>, que
    /// emite el escritor). El stream final no termina hasta que todas las mezclas terminen.
    /// <paramref name="toolApprovals"/> emite las aprobaciones pendientes (ver <see cref="UIMessageStream.ToChunks"/>).
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
    /// Crea un stream UI propio. Ejemplo:
    /// <code>
    /// var chunks = UIMessageStream.Create(async writer =>
    /// {
    ///     writer.WriteData("status", new { step = "buscando" }, id: "status");
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

    /// <summary>Convierte chunks del protocolo en eventos SSE (<c>data: {...}\n\n</c>), terminando en <c>[DONE]</c>.</summary>
    public static async IAsyncEnumerable<string> ToSseEvents(IAsyncEnumerable<JsonObject> chunks, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return "data: " + chunk.ToJsonString() + "\n\n";
        }

        yield return "data: [DONE]\n\n";
    }

    /// <summary>Escribe chunks del protocolo como SSE en <paramref name="output"/>.</summary>
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
/// Almacén de streams reanudables: guarda los eventos SSE de cada respuesta para que el cliente
/// pueda reconectarse (recargar la página, cambio de red) y continuar donde iba.
/// Implementa uno propio sobre Redis para varias instancias del servidor.
/// </summary>
public interface IResumableStreamStore
{
    Task CreateAsync(string streamId, CancellationToken cancellationToken = default);

    Task AppendAsync(string streamId, string sseEvent, CancellationToken cancellationToken = default);

    Task CompleteAsync(string streamId, CancellationToken cancellationToken = default);

    /// <summary>Devuelve los eventos desde el principio y sigue los nuevos hasta que el stream termina.</summary>
    IAsyncEnumerable<string> ReadAsync(string streamId, CancellationToken cancellationToken = default);

    Task SetActiveStreamAsync(string chatId, string? streamId, CancellationToken cancellationToken = default);

    Task<string?> GetActiveStreamAsync(string chatId, CancellationToken cancellationToken = default);
}

/// <summary>Almacén en memoria (una sola instancia del servidor). Los streams expiran tras <paramref name="ttl"/>.</summary>
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
    /// Clave del chat dentro del ámbito de su dueño. El id de chat lo elige el cliente, así que por sí solo no sirve
    /// para autorizar: combinándolo con el usuario (o una sesión que emitió el servidor) nadie puede reemplazar ni
    /// reanudar el stream de otro usuario aunque conozca o adivine su id de chat.
    /// </summary>
    public static string ScopedChatId(string owner, string chatId)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        return $"{owner.Length}:{owner}:{chatId}";
    }

    /// <summary>
    /// Empieza a volcar el stream en el almacén en segundo plano (sigue aunque el cliente se
    /// desconecte) y lo marca como el stream activo del chat. Devuelve el id del stream.
    /// <paramref name="chatId"/> es la clave del almacén: en un servidor con varios usuarios use
    /// <see cref="ScopedChatId"/> (los endpoints de <c>AiSdk.AspNetCore</c> ya lo hacen).
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
