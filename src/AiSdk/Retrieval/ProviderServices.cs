namespace AiSdk;

// ---------------------------------------------------------------------------
// Conteo de tokens
// ---------------------------------------------------------------------------

/// <summary>Modelo que puede contar los tokens de entrada antes de enviar la petición.</summary>
public interface ITokenCountingModel
{
    Task<int> CountTokensAsync(LanguageModelRequest request, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Archivos
// ---------------------------------------------------------------------------

/// <summary>Archivo subido a un proveedor (Files API).</summary>
public sealed record UploadedFile(string Id, string Filename, string? MediaType, long? SizeBytes, DateTimeOffset? CreatedAt);

/// <summary>Cliente de archivos de un proveedor (Anthropic Files API, OpenAI Files...).</summary>
public interface IFileClient
{
    /// <param name="data">Contenido del archivo.</param>
    /// <param name="filename">Nombre con extensión.</param>
    /// <param name="mediaType">Tipo MIME.</param>
    /// <param name="purpose">Propósito (solo OpenAI: <c>user_data</c>, <c>assistants</c>, <c>batch</c>...).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    Task<UploadedFile> UploadAsync(byte[] data, string filename, string mediaType, string? purpose = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadedFile>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileId, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Batches
// ---------------------------------------------------------------------------

/// <summary>Petición dentro de un lote.</summary>
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

/// <summary>Estado de un lote.</summary>
public sealed record BatchJob(string Id, BatchStatus Status, BatchCounts Counts)
{
    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public bool IsFinished => Status is BatchStatus.Completed or BatchStatus.Canceled or BatchStatus.Failed or BatchStatus.Expired;
}

/// <summary>Resultado de una petición del lote.</summary>
public sealed record BatchResultItem(string CustomId, LanguageModelResult? Result, string? Error)
{
    public bool Succeeded => Result is not null;

    public string Text => Result is null ? string.Empty : string.Concat(Result.Content.OfType<TextPart>().Select(t => t.Text));
}

/// <summary>
/// Procesamiento por lotes asíncrono (Message Batches de Anthropic, Batch API de OpenAI):
/// más barato que las llamadas normales a cambio de no ser inmediato.
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
    /// <summary>Consulta el estado periódicamente hasta que el lote termine.</summary>
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

    /// <summary>Crea un elemento de lote a partir de opciones de generación (sin bucle de herramientas).</summary>
    public static async Task<BatchRequestItem> ToBatchItemAsync(this GenerateTextOptions options, string customId)
    {
        var plan = await GenerationEngine.PrepareAsync(options, GenerationEngine.InitialMessages(options), []).ConfigureAwait(false);
        return new BatchRequestItem(customId, plan.Request);
    }
}
