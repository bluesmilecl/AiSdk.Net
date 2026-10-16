using AiSdk.Documents.Converters;
using AiSdk.Http;

namespace AiSdk.Documents;

/// <summary>
/// Convierte documentos de cualquier tipo a Markdown, el formato que los modelos entienden mejor y
/// con menos tokens. Equivalente a MarkItDown / Docling para .NET.
/// </summary>
/// <example>
/// <code>
/// var converter = new MarkdownConverter(new() { Ocr = new TesseractOcr("spa+eng") });
/// var result = await converter.ConvertFileAsync("contrato.pdf");
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

    /// <summary>Convertidores registrados, en orden de prioridad.</summary>
    public IReadOnlyList<IDocumentConverter> Converters => _converters;

    /// <summary>Registra un convertidor con prioridad sobre los incluidos.</summary>
    public MarkdownConverter Use(IDocumentConverter converter)
    {
        _converters.Insert(0, converter);
        return this;
    }

    public bool CanConvert(string mediaType) => _converters.Any(c => c.CanConvert(MediaTypes.Normalize(mediaType)));

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        var converter = _converters.FirstOrDefault(c => c.CanConvert(source.MediaType))
                        ?? throw new NotSupportedException($"No hay un convertidor para '{source.MediaType}' ({source.FileName}).");
        return await converter.ConvertAsync(source, new DocumentConversionContext(Options, this), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentConversionResult> ConvertFileAsync(string path, CancellationToken cancellationToken = default) =>
        await ConvertAsync(await DocumentSource.FromFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public Task<DocumentConversionResult> ConvertAsync(byte[] data, string? mediaType = null, string? fileName = null, CancellationToken cancellationToken = default) =>
        ConvertAsync(new DocumentSource(data, mediaType, fileName), cancellationToken);

    /// <summary>
    /// Convierte una parte de mensaje (<see cref="ImagePart"/> o <see cref="FilePart"/>) con datos en
    /// línea o, si <paramref name="downloadUrls"/> es true, descargando su URL.
    /// </summary>
    public async Task<DocumentConversionResult> ConvertAsync(ContentPart part, bool downloadUrls = false, CancellationToken cancellationToken = default)
    {
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

            if (url.Scheme == "data")
            {
                var raw = url.OriginalString;
                var comma = raw.IndexOf(',', StringComparison.Ordinal);
                data = Convert.FromBase64String(raw[(comma + 1)..]);
            }
            else
            {
                data = await (Options.HttpClient ?? ProviderHttp.DefaultClient).GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                fileName ??= Path.GetFileName(url.LocalPath);
            }
        }

        return data is null
            ? throw new NotSupportedException("El adjunto no tiene datos (los archivos por FileId no se pueden convertir localmente).")
            : await ConvertAsync(new DocumentSource(data, mediaType, fileName), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Extensiones para indexar documentos en almacenes vectoriales.</summary>
public static class DocumentVectorStoreExtensions
{
    /// <summary>
    /// Convierte el documento a Markdown, lo divide por página y en fragmentos y lo guarda en el almacén.
    /// Devuelve el resultado de la conversión (útil para revisar avisos).
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
