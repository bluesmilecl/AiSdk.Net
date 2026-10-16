using System.Text;
using System.Text.Json.Nodes;
using AiSdk.UI;
using Microsoft.AspNetCore.Http;

namespace AiSdk.AspNetCore;

/// <summary>Resultados HTTP para devolver streams desde Minimal APIs o controladores.</summary>
public static class StreamResultExtensions
{
    /// <summary>
    /// Devuelve el stream en formato UI Message Stream (SSE), compatible con
    /// <c>useChat</c> de <c>@ai-sdk/react</c> (equivalente a <c>toUIMessageStreamResponse()</c>).
    /// </summary>
    public static IResult ToUIMessageStreamResult(this StreamTextResult result, string? messageId = null, Func<Exception, string>? onError = null) =>
        new UIMessageStreamHttpResult(result.FullStream, messageId, onError);

    /// <summary>Devuelve solo el texto como <c>text/plain</c> en streaming (equivalente a <c>toTextStreamResponse()</c>).</summary>
    public static IResult ToTextStreamResult(this StreamTextResult result) => new TextStreamHttpResult(result.TextStream);

    /// <summary>Escribe el stream UI directamente en la respuesta HTTP.</summary>
    public static Task WriteUIMessageStreamAsync(this HttpResponse response, StreamTextResult result, string? messageId = null, Func<Exception, string>? onError = null) =>
        new UIMessageStreamHttpResult(result.FullStream, messageId, onError).ExecuteAsync(response.HttpContext);

    /// <summary>
    /// Devuelve el stream como respuesta <b>reanudable</b>: la generación se guarda en
    /// <paramref name="store"/> y continúa aunque el cliente se desconecte; el cliente puede
    /// reconectarse con <see cref="UIMessageStreamResults.Resume"/> (<c>useChat({ resume: true })</c>).
    /// </summary>
    public static IResult ToResumableUIMessageStreamResult(
        this StreamTextResult result,
        IResumableStreamStore store,
        string chatId,
        string? messageId = null,
        Func<Exception, string>? onError = null) =>
        UIMessageStreamResults.Resumable(store, chatId, UIMessageStream.ToChunks(result.FullStream, messageId, onError));

    internal static void SetSseHeaders(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = UIMessageStream.ContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers[UIMessageStream.HeaderName] = UIMessageStream.HeaderValue;
    }

    private sealed class UIMessageStreamHttpResult(IAsyncEnumerable<StreamPart> parts, string? messageId, Func<Exception, string>? onError) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            SetSseHeaders(httpContext.Response);
            await UIMessageStream.WriteAsync(parts, httpContext.Response.Body, messageId, onError, httpContext.RequestAborted).ConfigureAwait(false);
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

/// <summary>Resultados HTTP para streams UI propios y reanudables.</summary>
public static class UIMessageStreamResults
{
    /// <summary>
    /// Stream UI compuesto con datos propios (<c>data-*</c>) y uno o varios streams de texto.
    /// </summary>
    public static IResult Create(Func<UIMessageStreamWriter, Task> execute, string? messageId = null, Func<Exception, string>? onError = null) =>
        new ChunksResult(ct => UIMessageStream.Create(execute, messageId, onError, ct));

    /// <summary>Envía chunks del protocolo y los guarda en el almacén para poder reanudarlos.</summary>
    public static IResult Resumable(IResumableStreamStore store, string chatId, IAsyncEnumerable<JsonObject> chunks) =>
        new ResumableResult(store, chatId, chunks);

    /// <summary>
    /// Endpoint de reanudación (<c>GET /api/chat/{id}/stream</c>): devuelve el stream activo del chat
    /// desde el principio, o 204 si no hay ninguno en curso.
    /// </summary>
    public static IResult Resume(IResumableStreamStore store, string chatId) => new ResumeResult(store, chatId);

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

    private sealed class ResumableResult(IResumableStreamStore store, string chatId, IAsyncEnumerable<JsonObject> chunks) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var streamId = await ResumableUIMessageStream.StartAsync(store, chatId, chunks).ConfigureAwait(false);
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

    private sealed class ResumeResult(IResumableStreamStore store, string chatId) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var streamId = await store.GetActiveStreamAsync(chatId, httpContext.RequestAborted).ConfigureAwait(false);
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
