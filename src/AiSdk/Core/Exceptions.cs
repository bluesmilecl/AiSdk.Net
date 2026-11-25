namespace AiSdk;

/// <summary>Base exception of the SDK.</summary>
public class AiSdkException : Exception
{
    public AiSdkException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>HTTP error returned by a provider's API.</summary>
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

    /// <summary>Gets whether the error is retryable: 408, 409, 429 and 5xx are considered retryable.</summary>
    public bool IsRetryable => StatusCode is 408 or 409 or 429 or >= 500;
}

/// <summary>The model did not produce an object that conforms to the schema.</summary>
public sealed class NoObjectGeneratedException : AiSdkException
{
    public NoObjectGeneratedException(string message, string? text, Exception? inner = null) : base(message, inner)
    {
        Text = text;
    }

    /// <summary>Gets the raw text returned by the model.</summary>
    public string? Text { get; }
}

/// <summary>The maximum number of retries was exceeded.</summary>
public sealed class RetryException : AiSdkException
{
    public RetryException(string message, IReadOnlyList<Exception> errors)
        : base(message, errors.Count > 0 ? errors[^1] : null)
    {
        Errors = errors;
    }

    public IReadOnlyList<Exception> Errors { get; }
}
