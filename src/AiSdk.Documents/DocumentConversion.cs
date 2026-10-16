namespace AiSdk.Documents;

/// <summary>Convierte un tipo de documento a Markdown.</summary>
public interface IDocumentConverter
{
    bool CanConvert(string mediaType);

    Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Origen del texto de una página.</summary>
public enum PageTextSource
{
    /// <summary>Texto extraído directamente del archivo.</summary>
    Text,

    /// <summary>Texto reconocido con OCR.</summary>
    Ocr,

    /// <summary>Descripción generada por un modelo con visión.</summary>
    Vision,

    /// <summary>Transcripción de audio.</summary>
    Transcript,
}

/// <summary>Página, hoja, diapositiva o segmento del documento convertido.</summary>
public sealed record DocumentPage(int Number, string Markdown, PageTextSource Source = PageTextSource.Text)
{
    /// <summary>Etiqueta legible (p. ej. "Hoja: Ventas", "Diapositiva 3", "00:01:30").</summary>
    public string? Label { get; init; }
}

/// <summary>Resultado de la conversión.</summary>
public sealed record DocumentConversionResult
{
    public required string Markdown { get; init; }

    public required string MediaType { get; init; }

    public string? FileName { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<DocumentPage> Pages { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Tokens aproximados del Markdown (ver <see cref="TokenEstimator"/>).</summary>
    public int EstimatedTokens => TokenEstimator.Estimate(Markdown);

    /// <summary>True si alguna parte se obtuvo con OCR, visión o transcripción.</summary>
    public bool UsedAi => Pages.Any(p => p.Source != PageTextSource.Text);

    /// <summary>Crea un resultado a partir de páginas, uniendo su Markdown.</summary>
    public static DocumentConversionResult FromPages(DocumentSource source, IReadOnlyList<DocumentPage> pages, string? title = null, IReadOnlyList<string>? warnings = null, string separator = "\n\n") =>
        new()
        {
            Markdown = string.Join(separator, pages.Select(p => p.Markdown.Trim()).Where(m => m.Length > 0)),
            MediaType = source.MediaType,
            FileName = source.FileName,
            Title = title,
            Pages = pages,
            Warnings = warnings ?? [],
        };

    /// <summary>
    /// Divide el resultado en documentos para un <see cref="IVectorStore"/>, por página y en
    /// fragmentos de <paramref name="maxChars"/>. Cada fragmento lleva metadatos <c>source</c>,
    /// <c>page</c> y <c>title</c>.
    /// </summary>
    public IReadOnlyList<VectorDocument> ToVectorDocuments(string idPrefix, int maxChars = 1500, int overlap = 150, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var documents = new List<VectorDocument>();
        var pages = Pages.Count > 0 ? Pages : [new DocumentPage(1, Markdown)];
        foreach (var page in pages)
        {
            var chunks = TextChunker.Split(page.Markdown, maxChars, overlap);
            for (var i = 0; i < chunks.Count; i++)
            {
                var meta = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>())
                {
                    ["page"] = page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (FileName is not null) meta["source"] = FileName;
                if (Title is not null) meta["title"] = Title;
                if (page.Label is not null) meta["section"] = page.Label;
                documents.Add(new VectorDocument($"{idPrefix}:p{page.Number}:c{i}", chunks[i]) { Metadata = meta });
            }
        }

        return documents;
    }
}

/// <summary>Cuándo aplicar OCR a las páginas de un PDF.</summary>
public enum PdfOcrMode
{
    /// <summary>Solo en páginas sin texto suficiente (escaneadas).</summary>
    Auto,

    /// <summary>Nunca (solo texto incrustado).</summary>
    Never,

    /// <summary>En todas las páginas (útil con OCR de modelos que entienden tablas y diseños).</summary>
    Always,
}

/// <summary>Opciones de <see cref="MarkdownConverter"/>.</summary>
public sealed class MarkdownConverterOptions
{
    /// <summary>Motor OCR para imágenes, páginas escaneadas e imágenes incrustadas.</summary>
    public IOcrEngine? Ocr { get; init; }

    /// <summary>Modelo con visión para describir imágenes, gráficos y fotogramas de video.</summary>
    public ILanguageModel? VisionModel { get; init; }

    /// <summary>Instrucción para describir imágenes con <see cref="VisionModel"/>.</summary>
    public string ImageDescriptionPrompt { get; init; } =
        "Describe esta imagen para alguien que no puede verla, en el idioma del texto que contenga (o en español). " +
        "Incluye el texto visible, datos de gráficos o tablas, y elementos relevantes. Sé conciso y objetivo, en Markdown.";

    /// <summary>Modelo de transcripción para audio y video.</summary>
    public ITranscriptionModel? Transcription { get; init; }

    /// <summary>Idioma ISO-639-1 esperado (OCR y transcripción).</summary>
    public string? Language { get; init; }

    public PdfOcrMode PdfOcr { get; init; } = PdfOcrMode.Auto;

    /// <summary>Con <see cref="PdfOcrMode.Auto"/>, mínimo de caracteres para considerar que una página tiene texto.</summary>
    public int PdfMinCharsPerPage { get; init; } = 30;

    /// <summary>Rasterizador de páginas PDF para OCR. Por defecto <c>pdftoppm</c> si está instalado.</summary>
    public IPdfRasterizer? PdfRasterizer { get; init; } = PdftoppmRasterizer.TryCreate();

    /// <summary>Filas máximas por hoja de cálculo (el resto se resume con un aviso).</summary>
    public int MaxRowsPerSheet { get; init; } = 1000;

    public bool IncludeHiddenSheets { get; init; }

    /// <summary>Procesa las imágenes incrustadas en Word, PowerPoint y PDF (OCR y/o descripción).</summary>
    public bool ProcessEmbeddedImages { get; init; } = true;

    /// <summary>Imágenes incrustadas más pequeñas que esto (bytes) se ignoran (iconos, viñetas...).</summary>
    public int MinEmbeddedImageBytes { get; init; } = 4096;

    /// <summary>Ruta de <c>ffmpeg</c> (video).</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>Intervalo entre fotogramas analizados de un video.</summary>
    public TimeSpan VideoFrameInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxVideoFrames { get; init; } = 30;

    /// <summary>Llamadas de OCR / visión en paralelo.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Cliente para descargar adjuntos por URL.</summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>Contexto que reciben los convertidores: opciones y servicios de IA compartidos.</summary>
public sealed class DocumentConversionContext(MarkdownConverterOptions options, MarkdownConverter converter)
{
    public MarkdownConverterOptions Options { get; } = options;

    /// <summary>Convertidor principal (para documentos anidados).</summary>
    public MarkdownConverter Converter { get; } = converter;

    public bool CanAnalyzeImages => Options.Ocr is not null || Options.VisionModel is not null;

    /// <summary>OCR de una imagen o documento; null si no hay motor configurado.</summary>
    public async Task<OcrResult?> OcrAsync(byte[] data, string mediaType, CancellationToken cancellationToken)
    {
        if (Options.Ocr is not { } ocr || !ocr.Supports(mediaType))
        {
            return null;
        }

        return await ocr.RecognizeAsync(new OcrRequest { Data = data, MediaType = mediaType, Language = Options.Language }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Descripción de una imagen con el modelo de visión; null si no hay modelo configurado.</summary>
    public async Task<string?> DescribeImageAsync(byte[] data, string mediaType, string? instructions, CancellationToken cancellationToken)
    {
        if (Options.VisionModel is not { } model)
        {
            return null;
        }

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Messages = [ModelMessage.User(new TextPart(instructions ?? Options.ImageDescriptionPrompt), ImagePart.FromBytes(data, mediaType))],
            Temperature = 0,
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        return result.Text.Trim();
    }

    /// <summary>
    /// Analiza una imagen con lo que esté configurado: el modelo de visión si existe (describe e incluye
    /// el texto) o, si no, OCR. Devuelve null si no hay nada configurado.
    /// </summary>
    public async Task<(string Markdown, PageTextSource Source)?> AnalyzeImageAsync(byte[] data, string mediaType, CancellationToken cancellationToken)
    {
        if (Options.VisionModel is not null)
        {
            var description = await DescribeImageAsync(data, mediaType, null, cancellationToken).ConfigureAwait(false);
            return (description ?? string.Empty, PageTextSource.Vision);
        }

        var ocr = await OcrAsync(data, mediaType, cancellationToken).ConfigureAwait(false);
        return ocr is null ? null : (ocr.Markdown, PageTextSource.Ocr);
    }
}
