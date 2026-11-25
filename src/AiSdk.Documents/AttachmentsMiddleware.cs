using System.Collections.Concurrent;
using System.Security.Cryptography;
using AiSdk.Documents.Resources;

namespace AiSdk.Documents;

/// <summary>Attachment types the model understands natively (they are not converted).</summary>
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

/// <summary>Options for <see cref="DocumentMiddlewares.ConvertAttachments(MarkdownConverter, AttachmentConversionOptions?)"/>.</summary>
public sealed partial class AttachmentConversionOptions
{
    /// <summary>What the model processes by itself. None by default: everything is converted to text.</summary>
    public NativeAttachments Native { get; init; } = NativeAttachments.None;

    /// <summary>Maximum tokens per converted attachment. If exceeded, the attachment is compressed (see below) or truncated.</summary>
    public int? MaxTokensPerAttachment { get; init; }

    /// <summary>
    /// Extractive compression: keeps the chunks most relevant to the user's last message.
    /// Takes precedence over <see cref="Summarizer"/>.
    /// </summary>
    public IEmbeddingModel? CompressionEmbeddings { get; init; }

    /// <summary>Abstractive compression: summarizes the attachment with this model.</summary>
    public ILanguageModel? Summarizer { get; init; }

    /// <summary>
    /// Downloads attachments referenced by URL (disabled by default). Uses the converter's downloader
    /// (<see cref="MarkdownConverterOptions.UrlDownload"/>): public http/https only, no internal networks or cloud
    /// metadata, with bounded size, time and redirects.
    /// </summary>
    public bool DownloadUrls { get; init; }

    /// <summary>
    /// Format of the text inserted in place of the attachment. The name and content are controlled by whoever sends the
    /// file: a custom format must escape them (see <see cref="DefaultFormat"/>).
    /// </summary>
    public Func<DocumentConversionResult, string> Format { get; init; } = DefaultFormat;

    /// <summary>Maximum entries in the conversion cache (reused across turns).</summary>
    public int CacheSize { get; init; } = 256;

    /// <summary>
    /// <c>&lt;document name="..." type="..."&gt;content&lt;/document&gt;</c>. The name and type are escaped as
    /// attributes and the <c>&lt;document</c> / <c>&lt;/document</c> tags in the content are neutralized
    /// (<c>&amp;lt;/document</c>), so a file cannot close the wrapper and inject text outside it.
    /// </summary>
    public static string DefaultFormat(DocumentConversionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var name = EscapeAttribute(result.FileName ?? result.Title ?? Strings.Attachment_DefaultName);
        var warnings = result.Warnings.Count == 0
            ? string.Empty
            : $"\n<!-- {Strings.Attachment_Warnings}: {NeutralizeTags(string.Join(" | ", result.Warnings)).Replace("--", "- -", StringComparison.Ordinal)} -->";
        return $"<document name=\"{name}\" type=\"{EscapeAttribute(result.MediaType)}\">\n{NeutralizeTags(result.Markdown)}{warnings}\n</document>";
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

    /// <summary>Turns <c>&lt;document</c> and <c>&lt;/document</c> (with or without spaces, in any case) into <c>&amp;lt;...</c>.</summary>
    private static string NeutralizeTags(string text) => DocumentTag().Replace(text, "&lt;");

    // Grupos atómicos: sin retroceso cuadrático con muchos espacios.
    [System.Text.RegularExpressions.GeneratedRegex(@"<(?=(?>\s*)/?(?>\s*)document\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DocumentTag();
}

/// <summary>Document middlewares.</summary>
public static class DocumentMiddlewares
{
    /// <summary>
    /// Replaces images, PDFs, Office documents, audio and video in messages with their Markdown version (OCR,
    /// vision-based description, transcription). Lets you use models without vision or file support (or cheaper
    /// ones) with any attachment, and uses fewer tokens than sending page images. Conversions are cached, so the
    /// history is not reprocessed.
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
                markdown = markdown[..Math.Min(markdown.Length, max * 4)] + "\n\n" + Strings.Attachment_Truncated;
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
