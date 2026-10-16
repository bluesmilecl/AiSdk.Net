namespace AiSdk;

/// <summary>Excepción base del SDK.</summary>
public class AiSdkException : Exception
{
    public AiSdkException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Error HTTP devuelto por la API de un proveedor.</summary>
public sealed class ApiCallException : AiSdkException
{
    public ApiCallException(string message, int statusCode, string? responseBody, Uri? url, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Url = url;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }

    public string? ResponseBody { get; }

    public Uri? Url { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>408, 409, 429 y 5xx se consideran reintentables.</summary>
    public bool IsRetryable => StatusCode is 408 or 409 or 429 or >= 500;
}

/// <summary>El modelo no produjo un objeto que cumpla el esquema.</summary>
public sealed class NoObjectGeneratedException : AiSdkException
{
    public NoObjectGeneratedException(string message, string? text, Exception? inner = null) : base(message, inner)
    {
        Text = text;
    }

    /// <summary>Texto crudo devuelto por el modelo.</summary>
    public string? Text { get; }
}

/// <summary>Se superó el número de reintentos.</summary>
public sealed class RetryException : AiSdkException
{
    public RetryException(string message, IReadOnlyList<Exception> errors)
        : base(message, errors.Count > 0 ? errors[^1] : null)
    {
        Errors = errors;
    }

    public IReadOnlyList<Exception> Errors { get; }
}
