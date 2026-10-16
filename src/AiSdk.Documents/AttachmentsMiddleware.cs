using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AiSdk.Documents;

/// <summary>Tipos de adjunto que el modelo entiende de forma nativa (no se convierten).</summary>
[Flags]
public enum NativeAttachments
{
    None = 0,
    Images = 1,
    Pdf = 2,
    Audio = 4,
    Video = 8,
    All = Images | Pdf | Audio | Video,
}

/// <summary>Opciones de <see cref="DocumentMiddlewares.ConvertAttachments(MarkdownConverter, AttachmentConversionOptions?)"/>.</summary>
public sealed class AttachmentConversionOptions
{
    /// <summary>Lo que el modelo procesa por sí mismo. Por defecto nada: todo se convierte a texto.</summary>
    public NativeAttachments Native { get; init; } = NativeAttachments.None;

    /// <summary>Tokens máximos por adjunto convertido. Si se supera, se comprime (ver abajo) o se recorta.</summary>
    public int? MaxTokensPerAttachment { get; init; }

    /// <summary>
    /// Compresión extractiva: conserva los fragmentos más relevantes para el último mensaje del usuario.
    /// Tiene prioridad sobre <see cref="Summarizer"/>.
    /// </summary>
    public IEmbeddingModel? CompressionEmbeddings { get; init; }

    /// <summary>Compresión abstractiva: resume el adjunto con este modelo.</summary>
    public ILanguageModel? Summarizer { get; init; }

    /// <summary>Descarga adjuntos referenciados por URL (desactivado por defecto para evitar SSRF).</summary>
    public bool DownloadUrls { get; init; }

    /// <summary>Formato del texto insertado en lugar del adjunto.</summary>
    public Func<DocumentConversionResult, string> Format { get; init; } = DefaultFormat;

    /// <summary>Entradas máximas de la caché de conversiones (se reutilizan entre turnos).</summary>
    public int CacheSize { get; init; } = 256;

    public static string DefaultFormat(DocumentConversionResult result)
    {
        var name = result.FileName ?? result.Title ?? "adjunto";
        var warnings = result.Warnings.Count == 0 ? string.Empty : $"\n<!-- avisos: {string.Join(" | ", result.Warnings)} -->";
        return $"<document name=\"{name}\" type=\"{result.MediaType}\">\n{result.Markdown}{warnings}\n</document>";
    }
}

/// <summary>Middlewares de documentos.</summary>
public static class DocumentMiddlewares
{
    /// <summary>
    /// Reemplaza imágenes, PDF, documentos Office, audio y video de los mensajes por su versión en
    /// Markdown (OCR, descripción con visión, transcripción). Permite usar modelos sin visión ni
    /// soporte de archivos —o más baratos— con cualquier adjunto, y reduce tokens frente a enviar
    /// imágenes de páginas. Las conversiones se cachean, así que el historial no se reprocesa.
    /// </summary>
    public static ILanguageModelMiddleware ConvertAttachments(MarkdownConverter converter, AttachmentConversionOptions? options = null) =>
        new AttachmentConversionMiddleware(converter, options ?? new AttachmentConversionOptions());
}

internal sealed class AttachmentConversionMiddleware(MarkdownConverter converter, AttachmentConversionOptions options) : ILanguageModelMiddleware
{
    private readonly ConcurrentDictionary<string, DocumentConversionResult> _cache = new();

    public async ValueTask<LanguageModelRequest> TransformRequestAsync(LanguageModelRequest request, ILanguageModel model, CancellationToken cancellationToken)
    {
        if (!request.Messages.Any(m => m.Content.Any(ShouldConvert)))
        {
            return request;
        }

        var query = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content.OfType<TextPart>().Select(t => t.Text).LastOrDefault() ?? string.Empty;
        var messages = new List<ModelMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            if (!message.Content.Any(ShouldConvert))
            {
                messages.Add(message);
                continue;
            }

            var parts = new List<ContentPart>(message.Content.Count);
            foreach (var part in message.Content)
            {
                parts.Add(ShouldConvert(part) ? await ConvertAsync(part, query, cancellationToken).ConfigureAwait(false) : part);
            }

            messages.Add(message with { Content = parts });
        }

        return request with { Messages = messages };
    }

    private bool ShouldConvert(ContentPart part)
    {
        var (mediaType, hasData, hasUrl) = part switch
        {
            ImagePart image => (image.MediaType, image.Data is not null, image.Url is not null),
            FilePart file => (file.MediaType, file.Data is not null, file.Url is not null),
            _ => (null, false, false),
        };
        if (mediaType is null || !(hasData || (hasUrl && options.DownloadUrls)))
        {
            return false;
        }

        mediaType = MediaTypes.Normalize(mediaType);
        if (mediaType == "application/octet-stream" && part is FilePart { Data: { } bytes } filePart)
        {
            mediaType = MediaTypes.Detect(bytes, filePart.Filename);
        }

        var native = options.Native;
        var isNative = (MediaTypes.IsImage(mediaType) && native.HasFlag(NativeAttachments.Images))
                       || (mediaType == MediaTypes.Pdf && native.HasFlag(NativeAttachments.Pdf))
                       || (MediaTypes.IsAudio(mediaType) && native.HasFlag(NativeAttachments.Audio))
                       || (MediaTypes.IsVideo(mediaType) && native.HasFlag(NativeAttachments.Video));
        return !isNative && converter.CanConvert(mediaType);
    }

    private async Task<ContentPart> ConvertAsync(ContentPart part, string query, CancellationToken cancellationToken)
    {
        var key = Key(part);
        if (!_cache.TryGetValue(key, out var result))
        {
            result = await converter.ConvertAsync(part, options.DownloadUrls, cancellationToken).ConfigureAwait(false);
            if (_cache.Count >= options.CacheSize)
            {
                _cache.Clear();
            }

            _cache[key] = result;
        }

        var markdown = result.Markdown;
        if (options.MaxTokensPerAttachment is { } max && TokenEstimator.Estimate(markdown) > max)
        {
            if (options.CompressionEmbeddings is { } embeddings && query.Length > 0)
            {
                markdown = await ContextCompressor.SelectRelevantAsync(embeddings, markdown, query, max, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (options.Summarizer is { } summarizer)
            {
                var summaryKey = key + ":summary:" + max;
                if (_cache.TryGetValue(summaryKey, out var cached))
                {
                    markdown = cached.Markdown;
                }
                else
                {
                    markdown = await ContextCompressor.SummarizeAsync(summarizer, markdown, max, cancellationToken: cancellationToken).ConfigureAwait(false);
                    _cache[summaryKey] = result with { Markdown = markdown };
                }
            }
            else
            {
                markdown = markdown[..Math.Min(markdown.Length, max * 4)] + "\n\n[… contenido recortado por longitud]";
            }
        }

        return new TextPart(options.Format(result with { Markdown = markdown }));
    }

    private static string Key(ContentPart part)
    {
        var (data, url, mediaType) = part switch
        {
            ImagePart image => (image.Data, image.Url, image.MediaType),
            FilePart file => (file.Data, file.Url, file.MediaType),
            _ => (null, null, string.Empty),
        };
        return data is not null ? $"{mediaType}:{Convert.ToHexString(SHA256.HashData(data))}" : $"{mediaType}:{url}";
    }
}
