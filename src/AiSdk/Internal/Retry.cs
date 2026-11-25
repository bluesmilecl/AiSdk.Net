using System.Diagnostics;

namespace AiSdk.Internal;

internal static class Retry
{
    /// <summary>Maximum <c>Retry-After</c> wait accepted when the caller does not specify another.</summary>
    public static readonly TimeSpan DefaultMaxRetryAfter = TimeSpan.FromSeconds(60);

    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, int maxRetries, CancellationToken cancellationToken, TimeSpan? maxRetryAfter = null)
    {
        var errors = new List<Exception>();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex) && !cancellationToken.IsCancellationRequested)
            {
                errors.Add(ex);
                if (attempt >= maxRetries)
                {
                    if (maxRetries == 0)
                    {
                        throw;
                    }

                    throw new RetryException($"Falló tras {attempt + 1} intentos: {ex.Message}", errors);
                }

                if (!TryGetDelay(attempt, ex, maxRetryAfter ?? DefaultMaxRetryAfter, out var delay))
                {
                    // El servidor pidió esperar más de lo aceptado: no se reintenta antes de tiempo (eso solo
                    // alarga el bloqueo); se informa el error con su RetryAfter para que el llamador decida.
                    if (attempt == 0)
                    {
                        throw;
                    }

                    throw new RetryException($"Falló tras {attempt + 1} intentos; el servidor pidió esperar {delay.TotalSeconds:0} s antes de reintentar: {ex.Message}", errors);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static bool IsRetryable(Exception ex) => ex switch
    {
        ApiCallException api => api.IsRetryable,
        HttpRequestException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// Gets the delay before the retry: the server's <c>Retry-After</c> (never negative) or an exponential backoff with
    /// random jitter. Returns false if the server asks to wait longer than <paramref name="maxRetryAfter"/>.
    /// </summary>
    public static bool TryGetDelay(int attempt, Exception ex, TimeSpan maxRetryAfter, out TimeSpan delay)
    {
        if (ex is ApiCallException { RetryAfter: { } retryAfter })
        {
            delay = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
            return delay <= maxRetryAfter;
        }

        var baseMs = 2000 * Math.Pow(2, attempt);
        delay = TimeSpan.FromMilliseconds(baseMs * (0.8 + (Random.Shared.NextDouble() * 0.4)));
        return true;
    }
}
