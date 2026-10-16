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

    private sealed class UIMessageStreamHttpResult(IAsyncEnumerable<StreamPart> parts, string? messageId, Func<Exception, string>? onError) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = UIMessageStream.ContentType;
            response.Headers.CacheControl = "no-cache";
            response.Headers.Connection = "keep-alive";
            response.Headers["X-Accel-Buffering"] = "no";
            response.Headers[UIMessageStream.HeaderName] = UIMessageStream.HeaderValue;
            await UIMessageStream.WriteAsync(parts, response.Body, messageId, onError, httpContext.RequestAborted).ConfigureAwait(false);
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
