namespace AiSdk;

// ---------------------------------------------------------------------------
// Conteo de tokens
// ---------------------------------------------------------------------------

/// <summary>Model that can count the input tokens before sending the request.</summary>
public interface ITokenCountingModel
{
    Task<int> CountTokensAsync(LanguageModelRequest request, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Archivos
// ---------------------------------------------------------------------------

/// <summary>File uploaded to a provider (Files API).</summary>
public sealed record UploadedFile(string Id, string Filename, string? MediaType, long? SizeBytes, DateTimeOffset? CreatedAt);

/// <summary>A provider's file client (Anthropic Files API, OpenAI Files...).</summary>
public interface IFileClient
{
    /// <param name="data">File content.</param>
    /// <param name="filename">File name with extension.</param>
    /// <param name="mediaType">MIME type.</param>
    /// <param name="purpose">Purpose (OpenAI only: <c>user_data</c>, <c>assistants</c>, <c>batch</c>...).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UploadedFile> UploadAsync(byte[] data, string filename, string mediaType, string? purpose = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadedFile>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileId, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Batches
// ---------------------------------------------------------------------------

/// <summary>Request within a batch.</summary>
public sealed record BatchRequestItem(string CustomId, LanguageModelRequest Request);

public enum BatchStatus
{
    InProgress,
    Canceling,
    Completed,
    Canceled,
    Failed,
    Expired,
}

public sealed record BatchCounts(int Total, int Succeeded, int Failed);

/// <summary>Status of a batch.</summary>
public sealed record BatchJob(string Id, BatchStatus Status, BatchCounts Counts)
{
    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public bool IsFinished => Status is BatchStatus.Completed or BatchStatus.Canceled or BatchStatus.Failed or BatchStatus.Expired;
}

/// <summary>Result of a request in the batch.</summary>
public sealed record BatchResultItem(string CustomId, LanguageModelResult? Result, string? Error)
{
    public bool Succeeded => Result is not null;

    public string Text => Result is null ? string.Empty : string.Concat(Result.Content.OfType<TextPart>().Select(t => t.Text));
}

/// <summary>
/// Asynchronous batch processing (Anthropic Message Batches, OpenAI Batch API):
/// cheaper than regular calls in exchange for not being immediate.
/// </summary>
public interface IBatchClient
{
    Task<BatchJob> CreateAsync(IReadOnlyList<BatchRequestItem> requests, CancellationToken cancellationToken = default);

    Task<BatchJob> GetAsync(string batchId, CancellationToken cancellationToken = default);

    Task<BatchJob> CancelAsync(string batchId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<BatchResultItem> GetResultsAsync(string batchId, CancellationToken cancellationToken = default);
}

public static class BatchClientExtensions
{
    /// <summary>Polls the status periodically until the batch finishes.</summary>
    public static async Task<BatchJob> WaitForCompletionAsync(
        this IBatchClient client,
        string batchId,
        TimeSpan? pollInterval = null,
        IProgress<BatchJob>? progress = null,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var job = await client.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            progress?.Report(job);
            if (job.IsFinished)
            {
                return job;
            }

            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Creates a batch item from generation options (without the tool loop).</summary>
    public static async Task<BatchRequestItem> ToBatchItemAsync(this GenerateTextOptions options, string customId)
    {
        var plan = await GenerationEngine.PrepareAsync(options, GenerationEngine.InitialMessages(options), []).ConfigureAwait(false);
        return new BatchRequestItem(customId, plan.Request);
    }
}
