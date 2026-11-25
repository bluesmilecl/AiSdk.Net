using System.Text;
using System.Text.Json.Nodes;
using AiSdk.UI;
using Microsoft.AspNetCore.Http;

namespace AiSdk.AspNetCore;

/// <summary>HTTP results for returning streams from Minimal APIs or controllers.</summary>
public static class StreamResultExtensions
{
    /// <summary>
    /// Returns the stream in UI Message Stream format (SSE), compatible with
    /// <c>useChat</c> from <c>@ai-sdk/react</c> (equivalent to <c>toUIMessageStreamResponse()</c>).
    /// With <paramref name="toolApprovals"/> (<see cref="ToolApprovalProtection.ForChat"/>), calls awaiting
    /// approval reach <c>useChat</c> in the <c>approval-requested</c> state.
    /// </summary>
    public static IResult ToUIMessageStreamResult(
        this StreamTextResult result,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        ToolApprovalProtector? toolApprovals = null) =>
        new UIMessageStreamHttpResult(result.FullStream, messageId, onError, toolApprovals);

    /// <summary>Returns only the text as streaming <c>text/plain</c> (equivalent to <c>toTextStreamResponse()</c>).</summary>
    public static IResult ToTextStreamResult(this StreamTextResult result) => new TextStreamHttpResult(result.TextStream);

    /// <summary>Writes the UI stream directly to the HTTP response.</summary>
    public static Task WriteUIMessageStreamAsync(
        this HttpResponse response,
        StreamTextResult result,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        ToolApprovalProtector? toolApprovals = null) =>
        new UIMessageStreamHttpResult(result.FullStream, messageId, onError, toolApprovals).ExecuteAsync(response.HttpContext);

    /// <summary>
    /// Returns the stream as a <b>resumable</b> response: the generation is stored in
    /// <paramref name="store"/> and continues even if the client disconnects; the client can
    /// reconnect with <see cref="UIMessageStreamResults.Resume"/> (<c>useChat({ resume: true })</c>).
    /// The stream is stored under the chat <b>and its owner</b> (<paramref name="owner"/> or, if null, the authenticated
    /// user): another user with the same chat id cannot replace or resume it.
    /// Without an owner the response fails: in anonymous applications, pass a server-issued session id.
    /// <paramref name="toolApprovals"/>: see <see cref="ToUIMessageStreamResult"/>.
    /// </summary>
    public static IResult ToResumableUIMessageStreamResult(
        this StreamTextResult result,
        IResumableStreamStore store,
        string chatId,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        string? owner = null,
        ToolApprovalProtector? toolApprovals = null) =>
        UIMessageStreamResults.Resumable(store, chatId, UIMessageStream.ToChunks(result.FullStream, messageId, onError, toolApprovals), owner);

    internal static void SetSseHeaders(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = UIMessageStream.ContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers[UIMessageStream.HeaderName] = UIMessageStream.HeaderValue;
    }

    private sealed class UIMessageStreamHttpResult(
        IAsyncEnumerable<StreamPart> parts,
        string? messageId,
        Func<Exception, string>? onError,
        ToolApprovalProtector? toolApprovals) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            SetSseHeaders(httpContext.Response);
            await UIMessageStream.WriteAsync(parts, httpContext.Response.Body, messageId, onError, toolApprovals, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed class TextStreamHttpResult(IAsyncEnumerable<string> text) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var chunk in text.WithCancellation(httpContext.RequestAborted).ConfigureAwait(false))
            {
                await response.WriteAsync(chunk, httpContext.RequestAborted).ConfigureAwait(false);
                await response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>HTTP results for custom and resumable UI streams.</summary>
public static class UIMessageStreamResults
{
    /// <summary>
    /// UI stream composed of custom data (<c>data-*</c>) and one or more text streams.
    /// </summary>
    public static IResult Create(Func<UIMessageStreamWriter, Task> execute, string? messageId = null, Func<Exception, string>? onError = null) =>
        new ChunksResult(ct => UIMessageStream.Create(execute, messageId, onError, ct));

    /// <summary>
    /// Sends protocol chunks and stores them in the store so they can be resumed, under the chat and its owner
    /// (<paramref name="owner"/> or, if null, the authenticated user; fails without an owner).
    /// </summary>
    public static IResult Resumable(IResumableStreamStore store, string chatId, IAsyncEnumerable<JsonObject> chunks, string? owner = null) =>
        new ResumableResult(store, chatId, chunks, owner);

    /// <summary>
    /// Resume endpoint (<c>GET /api/chat/{id}/stream</c>): returns the chat's active stream
    /// from the beginning, or 204 if none is in progress. It only finds streams of the same owner
    /// (<paramref name="owner"/> or the authenticated user); a request without an owner always gets 204.
    /// </summary>
    public static IResult Resume(IResumableStreamStore store, string chatId, string? owner = null) => new ResumeResult(store, chatId, owner);

    /// <summary>Chat key in the store: the client's chat id within its owner's scope.</summary>
    private static string? ScopedKey(HttpContext http, string chatId, string? owner) =>
        (owner ?? EndpointSecurity.GetUserId(http)) is { Length: > 0 } scope ? ResumableUIMessageStream.ScopedChatId(scope, chatId) : null;

    private static async Task CopyAsync(IAsyncEnumerable<string> events, HttpContext http)
    {
        StreamResultExtensions.SetSseHeaders(http.Response);
        await foreach (var sse in events.WithCancellation(http.RequestAborted).ConfigureAwait(false))
        {
            await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(sse), http.RequestAborted).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed class ChunksResult(Func<CancellationToken, IAsyncEnumerable<JsonObject>> chunks) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            StreamResultExtensions.SetSseHeaders(httpContext.Response);
            await UIMessageStream.WriteChunksAsync(chunks(httpContext.RequestAborted), httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed class ResumableResult(IResumableStreamStore store, string chatId, IAsyncEnumerable<JsonObject> chunks, string? owner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var key = ScopedKey(httpContext, chatId, owner)
                      ?? throw new InvalidOperationException("Los streams reanudables necesitan un dueño: autentique al usuario o indique 'owner' (p. ej. un id de sesión emitido por el servidor).");
            var streamId = await ResumableUIMessageStream.StartAsync(store, key, chunks).ConfigureAwait(false);
            try
            {
                await CopyAsync(store.ReadAsync(streamId), httpContext).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                // El cliente se desconectó: la generación sigue en segundo plano y se puede reanudar.
            }
        }
    }

    private sealed class ResumeResult(IResumableStreamStore store, string chatId, string? owner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var key = ScopedKey(httpContext, chatId, owner);
            var streamId = key is null ? null : await store.GetActiveStreamAsync(key, httpContext.RequestAborted).ConfigureAwait(false);
            if (streamId is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            try
            {
                await CopyAsync(store.ReadAsync(streamId), httpContext).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
            }
        }
    }
}
