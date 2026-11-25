using AiSdk.Documents.Converters;

namespace AiSdk.Documents;

/// <summary>
/// Converts documents of any type to Markdown, the format models understand best and with the fewest
/// tokens. The .NET equivalent of MarkItDown / Docling.
/// </summary>
/// <example>
/// <code>
/// var converter = new MarkdownConverter(new() { Ocr = new TesseractOcr("spa+eng") });
/// var result = await converter.ConvertFileAsync("contract.pdf");
/// Console.WriteLine(result.Markdown);
/// </code>
/// </example>
public sealed class MarkdownConverter
{
    private readonly List<IDocumentConverter> _converters =
    [
        new PdfConverter(),
        new DocxConverter(),
        new XlsxConverter(),
        new PptxConverter(),
        new HtmlConverter(),
        new ImageConverter(),
        new AudioConverter(),
        new VideoConverter(),
        new PlainTextConverter(),
    ];

    public MarkdownConverter(MarkdownConverterOptions? options = null)
    {
        Options = options ?? new MarkdownConverterOptions();
    }

    public MarkdownConverterOptions Options { get; }

    /// <summary>Registered converters, in priority order.</summary>
    public IReadOnlyList<IDocumentConverter> Converters => _converters;

    /// <summary>Registers a converter that takes precedence over the built-in ones.</summary>
    public MarkdownConverter Use(IDocumentConverter converter)
    {
        _converters.Insert(0, converter);
        return this;
    }

    public bool CanConvert(string mediaType) => _converters.Any(c => c.CanConvert(MediaTypes.Normalize(mediaType)));

    /// <exception cref="DocumentTooLargeException">The document exceeds <see cref="MarkdownConverterOptions.MaxInputBytes"/>.</exception>
    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Data.LongLength > Options.MaxInputBytes)
        {
            throw new DocumentTooLargeException(source.Data.LongLength, Options.MaxInputBytes, source.FileName);
        }

        var converter = _converters.FirstOrDefault(c => c.CanConvert(source.MediaType))
                        ?? throw new NotSupportedException($"No hay un convertidor para '{source.MediaType}' ({source.FileName}).");
        return await converter.ConvertAsync(source, new DocumentConversionContext(Options, this), cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="DocumentTooLargeException">The file exceeds <see cref="MarkdownConverterOptions.MaxInputBytes"/> (checked before reading it).</exception>
    public async Task<DocumentConversionResult> ConvertFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var length = new FileInfo(path).Length;
        if (length > Options.MaxInputBytes)
        {
            throw new DocumentTooLargeException(length, Options.MaxInputBytes, Path.GetFileName(path));
        }

        return await ConvertAsync(await DocumentSource.FromFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public Task<DocumentConversionResult> ConvertAsync(byte[] data, string? mediaType = null, string? fileName = null, CancellationToken cancellationToken = default) =>
        ConvertAsync(new DocumentSource(data, mediaType, fileName), cancellationToken);

    /// <summary>
    /// Converts a message part (<see cref="ImagePart"/> or <see cref="FilePart"/>) with inline data or, if
    /// <paramref name="downloadUrls"/> is true, by downloading its URL with the protections of
    /// <see cref="MarkdownConverterOptions.UrlDownload"/> (public http/https only, bounded size and time; see
    /// <see cref="UrlDownloader"/>). <c>data:</c> URLs are decoded with the same size limit.
    /// </summary>
    /// <exception cref="UrlDownloadException">The URL is not allowed or the download failed.</exception>
    /// <exception cref="DocumentTooLargeException">The attachment exceeds the maximum size.</exception>
    public async Task<DocumentConversionResult> ConvertAsync(ContentPart part, bool downloadUrls = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        var (data, url, mediaType, fileName) = part switch
        {
            ImagePart image => (image.Data, image.Url, image.MediaType, (string?)null),
            FilePart file => (file.Data, file.Url, file.MediaType, file.Filename),
            _ => throw new NotSupportedException($"No se puede convertir una parte {part.GetType().Name}."),
        };

        if (data is null && url is not null)
        {
            if (!downloadUrls)
            {
                throw new NotSupportedException("El adjunto es una URL; habilite la descarga de URLs para convertirlo.");
            }

            var maxBytes = Math.Min(Options.UrlDownload.MaxBytes, Options.MaxInputBytes);
            var download = await UrlDownloader.DownloadAsync(url, Options.UrlDownload, maxBytes, cancellationToken).ConfigureAwait(false);
            data = download.Data;
            if (url.Scheme != "data")
            {
                fileName ??= Path.GetFileName(download.Url.LocalPath) is { Length: > 0 } name ? name : null;
            }
        }

        return data is null
            ? throw new NotSupportedException("El adjunto no tiene datos (los archivos por FileId no se pueden convertir localmente).")
            : await ConvertAsync(new DocumentSource(data, mediaType, fileName), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Extensions for indexing documents in vector stores.</summary>
public static class DocumentVectorStoreExtensions
{
    /// <summary>
    /// Converts the document to Markdown, splits it by page and into chunks, and saves it in the store.
    /// Returns the conversion result (useful for reviewing warnings).
    /// </summary>
    public static async Task<DocumentConversionResult> IngestAsync(
        this IVectorStore store,
        MarkdownConverter converter,
        DocumentSource source,
        string? id = null,
        int maxChars = 1500,
        int overlap = 150,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var result = await converter.ConvertAsync(source, cancellationToken).ConfigureAwait(false);
        var documents = result.ToVectorDocuments(id ?? source.FileName ?? Guid.NewGuid().ToString("N"), maxChars, overlap, metadata);
        await store.UpsertAsync(documents, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
