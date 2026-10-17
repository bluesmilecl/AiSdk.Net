using System.Runtime.CompilerServices;

namespace AiSdk;

/// <summary>
/// Middleware de modelo (equivalente a <c>wrapLanguageModel</c>). Permite transformar
/// peticiones, interceptar respuestas, cachear, registrar, aplicar guardrails, etc.
/// Todos los miembros tienen implementación por defecto: sobrescribe solo lo necesario.
/// </summary>
public interface ILanguageModelMiddleware
{
    ValueTask<LanguageModelRequest> TransformRequestAsync(LanguageModelRequest request, ILanguageModel model, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "\"next\" es el nombre convencional del siguiente eslabón en middleware (como en ASP.NET Core); no hay consumidores en Visual Basic.")]
    Task<LanguageModelResult> WrapGenerateAsync(
        LanguageModelRequest request,
        ILanguageModel model,
        Func<LanguageModelRequest, CancellationToken, Task<LanguageModelResult>> next,
        CancellationToken cancellationToken) => next(request, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "\"next\" es el nombre convencional del siguiente eslabón en middleware (como en ASP.NET Core); no hay consumidores en Visual Basic.")]
    IAsyncEnumerable<StreamPart> WrapStream(
        LanguageModelRequest request,
        ILanguageModel model,
        Func<LanguageModelRequest, CancellationToken, IAsyncEnumerable<StreamPart>> next,
        CancellationToken cancellationToken) => next(request, cancellationToken);
}

internal sealed class WrappedLanguageModel(ILanguageModel inner, ILanguageModelMiddleware middleware) : ILanguageModel, ITokenCountingModel
{
    public async Task<int> CountTokensAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var transformed = await middleware.TransformRequestAsync(request, inner, cancellationToken).ConfigureAwait(false);
        return inner is ITokenCountingModel counter
            ? await counter.CountTokensAsync(transformed, cancellationToken).ConfigureAwait(false)
            : throw new NotSupportedException($"El modelo '{inner.Provider}:{inner.ModelId}' no permite contar tokens.");
    }

    public string Provider => inner.Provider;

    public string ModelId => inner.ModelId;

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var transformed = await middleware.TransformRequestAsync(request, inner, cancellationToken).ConfigureAwait(false);
        return await middleware.WrapGenerateAsync(transformed, inner, inner.GenerateAsync, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var transformed = await middleware.TransformRequestAsync(request, inner, cancellationToken).ConfigureAwait(false);
        await foreach (var part in middleware.WrapStream(transformed, inner, inner.StreamAsync, cancellationToken).ConfigureAwait(false))
        {
            yield return part;
        }
    }
}

/// <summary>Middlewares incluidos.</summary>
public static partial class Middlewares
{
    /// <summary>Aplica valores por defecto a las peticiones (solo si no fueron especificados).</summary>
    public static ILanguageModelMiddleware DefaultSettings(
        double? temperature = null,
        int? maxOutputTokens = null,
        ProviderOptions? providerOptions = null) => new DefaultSettingsMiddleware(temperature, maxOutputTokens, providerOptions);

    /// <summary>Middleware a partir de una función de transformación de la petición.</summary>
    public static ILanguageModelMiddleware TransformRequest(Func<LanguageModelRequest, LanguageModelRequest> transform) =>
        new TransformMiddleware(transform);

    /// <summary>Registra cada llamada (petición y respuesta) mediante un callback.</summary>
    public static ILanguageModelMiddleware Logging(Action<string> log) => new LoggingMiddleware(log);

    private sealed class DefaultSettingsMiddleware(double? temperature, int? maxOutputTokens, ProviderOptions? providerOptions) : ILanguageModelMiddleware
    {
        public ValueTask<LanguageModelRequest> TransformRequestAsync(LanguageModelRequest request, ILanguageModel model, CancellationToken cancellationToken)
        {
            var merged = request.ProviderOptions;
            if (providerOptions is not null)
            {
                merged = new ProviderOptions();
                foreach (var (k, v) in providerOptions)
                {
                    merged[k] = (System.Text.Json.Nodes.JsonObject)v.DeepClone();
                }

                foreach (var (k, v) in request.ProviderOptions ?? [])
                {
                    if (merged.TryGetValue(k, out var existing))
                    {
                        Internal.JsonUtil.DeepMerge(existing, v);
                    }
                    else
                    {
                        merged[k] = v;
                    }
                }
            }

            return ValueTask.FromResult(request with
            {
                Temperature = request.Temperature ?? temperature,
                MaxOutputTokens = request.MaxOutputTokens ?? maxOutputTokens,
                ProviderOptions = merged,
            });
        }
    }

    private sealed class TransformMiddleware(Func<LanguageModelRequest, LanguageModelRequest> transform) : ILanguageModelMiddleware
    {
        public ValueTask<LanguageModelRequest> TransformRequestAsync(LanguageModelRequest request, ILanguageModel model, CancellationToken cancellationToken) =>
            ValueTask.FromResult(transform(request));
    }

    private sealed class LoggingMiddleware(Action<string> log) : ILanguageModelMiddleware
    {
        public async Task<LanguageModelResult> WrapGenerateAsync(
            LanguageModelRequest request,
            ILanguageModel model,
            Func<LanguageModelRequest, CancellationToken, Task<LanguageModelResult>> next,
            CancellationToken cancellationToken)
        {
            log($"[{model.Provider}:{model.ModelId}] generate: {request.Messages.Count} mensajes, {request.Tools?.Count ?? 0} herramientas");
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            log($"[{model.Provider}:{model.ModelId}] finish={result.FinishReason} in={result.Usage.InputTokens} out={result.Usage.OutputTokens}");
            return result;
        }

        public async IAsyncEnumerable<StreamPart> WrapStream(
            LanguageModelRequest request,
            ILanguageModel model,
            Func<LanguageModelRequest, CancellationToken, IAsyncEnumerable<StreamPart>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            log($"[{model.Provider}:{model.ModelId}] stream: {request.Messages.Count} mensajes, {request.Tools?.Count ?? 0} herramientas");
            await foreach (var part in next(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (part is ModelFinishPart f)
                {
                    log($"[{model.Provider}:{model.ModelId}] finish={f.FinishReason} in={f.Usage.InputTokens} out={f.Usage.OutputTokens}");
                }

                yield return part;
            }
        }
    }
}
