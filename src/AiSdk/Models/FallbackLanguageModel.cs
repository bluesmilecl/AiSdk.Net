using System.Runtime.CompilerServices;

namespace AiSdk;

/// <summary>
/// Modelo con conmutación por error (failover): prueba cada modelo en orden hasta que uno
/// responda. En streaming solo cambia de modelo si el fallo ocurre antes del primer fragmento.
/// </summary>
public sealed class FallbackLanguageModel : ILanguageModel
{
    private readonly IReadOnlyList<ILanguageModel> _models;
    private readonly Func<Exception, bool> _shouldFallback;

    /// <param name="models">Modelos en orden de preferencia (al menos uno).</param>
    /// <param name="shouldFallback">
    /// Decide si un error justifica probar el siguiente modelo. Por defecto: cualquier error
    /// salvo cancelaciones.
    /// </param>
    /// <param name="onFallback">Notificación opcional cada vez que se cambia de modelo.</param>
    public FallbackLanguageModel(
        IReadOnlyList<ILanguageModel> models,
        Func<Exception, bool>? shouldFallback = null,
        Action<ILanguageModel, Exception>? onFallback = null)
    {
        if (models.Count == 0)
        {
            throw new ArgumentException("Se necesita al menos un modelo.", nameof(models));
        }

        _models = models;
        _shouldFallback = shouldFallback ?? DefaultShouldFallback;
        OnFallback = onFallback;
    }

    public Action<ILanguageModel, Exception>? OnFallback { get; }

    public IReadOnlyList<ILanguageModel> Models => _models;

    public string Provider => _models[0].Provider;

    public string ModelId => _models[0].ModelId;

    public static bool DefaultShouldFallback(Exception ex) => ex is not OperationCanceledException;

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _models.Count; i++)
        {
            try
            {
                return await _models[i].GenerateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (i < _models.Count - 1 && _shouldFallback(ex) && !cancellationToken.IsCancellationRequested)
            {
                OnFallback?.Invoke(_models[i], ex);
            }
        }

        throw new InvalidOperationException("Inalcanzable.");
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _models.Count; i++)
        {
            var isLast = i == _models.Count - 1;
            await using var enumerator = _models[i].StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);

            // Se obtiene el primer elemento dentro de un try para poder cambiar de modelo.
            StreamPart? first = null;
            Exception? failure = null;
            try
            {
                if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    first = enumerator.Current;
                    if (first is ErrorPart e)
                    {
                        failure = e.Error;
                    }
                }
            }
            catch (Exception ex) when (!isLast && _shouldFallback(ex) && !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }

            if (failure is not null && !isLast && _shouldFallback(failure))
            {
                OnFallback?.Invoke(_models[i], failure);
                continue;
            }

            if (first is null)
            {
                yield break;
            }

            yield return first;
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }

            yield break;
        }
    }
}
