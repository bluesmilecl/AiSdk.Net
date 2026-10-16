using System.Diagnostics;

namespace AiSdk.Internal;

internal static class Retry
{
    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, int maxRetries, CancellationToken cancellationToken)
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

                await Task.Delay(Delay(attempt, ex), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static bool IsRetryable(Exception ex) => ex switch
    {
        ApiCallException api => api.IsRetryable,
        HttpRequestException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };

    public static TimeSpan Delay(int attempt, Exception ex)
    {
        if (ex is ApiCallException { RetryAfter: { } retryAfter } && retryAfter < TimeSpan.FromMinutes(1))
        {
            return retryAfter;
        }

        var baseMs = 2000 * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(baseMs * (0.8 + (Random.Shared.NextDouble() * 0.4)));
    }
}

/// <summary>Fuente de trazas OpenTelemetry del SDK (<c>"AiSdk"</c>).</summary>
public static class AiTelemetry
{
    public const string SourceName = "AiSdk";

    public static ActivitySource ActivitySource { get; } = new(SourceName, "0.1.0");
}
