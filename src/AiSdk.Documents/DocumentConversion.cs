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

/// <summary>
/// Avance de una conversión: páginas del documento, cuántas pasarán por OCR (o visión) y cuántas de esas ya
/// terminaron. Hoy lo informa la conversión de PDF.
/// </summary>
public sealed record DocumentConversionProgress(int Pages, int OcrPages, int OcrPagesDone);

/// <summary>
/// Opciones de <see cref="MarkdownConverter"/>. Los límites (<see cref="MaxInputBytes"/>, <see cref="MaxPdfPages"/>,
/// <see cref="MaxOcrPages"/>, <see cref="MaxNestingDepth"/>...) acotan el trabajo que un documento hostil puede provocar
/// —memoria, CPU, procesos externos y llamadas pagadas de OCR o visión—; al superarlos se convierte una parte y se
/// agrega un aviso a <see cref="DocumentConversionResult.Warnings"/>, salvo <see cref="MaxInputBytes"/>, que lanza
/// <see cref="DocumentTooLargeException"/>.
/// </summary>
public sealed class MarkdownConverterOptions
{
    /// <summary>
    /// Tamaño máximo de un documento, en bytes (por defecto 512 MB: alcanza para expedientes escaneados de cientos de
    /// páginas). Uno más grande lanza <see cref="DocumentTooLargeException"/> sin convertirse.
    /// </summary>
    public long MaxInputBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// Profundidad máxima de anidamiento (por defecto 128). En HTML, lo que esté más abajo se convierte a texto plano con
    /// un aviso. En Word, Excel y PowerPoint, una parte XML más profunda se rechaza con <see cref="InvalidDataException"/>:
    /// el SDK de Open XML la cargaría de forma recursiva y un archivo de pocos KB desbordaría la pila del proceso.
    /// </summary>
    public int MaxNestingDepth { get; init; } = 128;

    /// <summary>HTML: bytes máximos que se convierten (por defecto 10 MB); el resto se omite con un aviso.</summary>
    public int MaxHtmlBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Caracteres máximos de cada parte XML de Word, Excel y PowerPoint, ya descomprimida (por defecto 32 millones;
    /// <c>OpenSettings.MaxCharactersInPart</c>). Evita que un archivo pequeño y muy comprimido ocupe gigabytes. Una parte
    /// más grande lanza <see cref="InvalidDataException"/>, salvo las hojas de Excel, que se leen hasta ese punto.
    /// </summary>
    public long MaxOfficePartChars { get; init; } = 32_000_000;

    /// <summary>PDF: páginas máximas que se procesan (por defecto 2000); las demás se omiten con un aviso.</summary>
    public int MaxPdfPages { get; init; } = 2000;

    /// <summary>
    /// Páginas o imágenes máximas de un documento que se envían a OCR o al modelo de visión (por defecto 1000): páginas
    /// escaneadas de un PDF e imágenes incrustadas en Word, PowerPoint y PDF. Las demás se omiten con un aviso. Acota
    /// los procesos de OCR y las llamadas que puede provocar un documento hostil (las páginas totalmente en blanco, sin
    /// texto, imágenes ni dibujos, nunca van a OCR). Con un modelo de visión de pago conviene bajarlo. Con un OCR que lee
    /// el PDF completo (Mistral OCR) se cuentan todas sus páginas.
    /// </summary>
    public int MaxOcrPages { get; init; } = 1000;

    /// <summary>
    /// Tiempo máximo para el OCR de las páginas de un PDF (por defecto sin límite). Al cumplirse, las páginas que faltan
    /// quedan sin reconocer y el resultado lleva un aviso con cuántas se leyeron: mejor un documento largo leído en parte
    /// que una conversión cancelada sin nada. Úselo por debajo del tiempo máximo que la aplicación da a una conversión.
    /// </summary>
    public TimeSpan? MaxOcrDuration { get; init; }

    /// <summary>
    /// Lado mayor máximo, en píxeles, de una página de PDF rasterizada para OCR (por defecto 5000). Las páginas se
    /// rasterizan a 200 ppp; una página enorme se rasteriza a menos resolución para no exceder este tamaño. Las imágenes
    /// incrustadas de más de este tamaño al cuadrado (en píxeles) se ignoran.
    /// </summary>
    public int MaxRasterPixels { get; init; } = 5000;

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

    /// <summary>
    /// Rasterizador de páginas PDF para OCR. Por defecto <c>pdftoppm</c> si está instalado. Cada página escaneada se
    /// rasteriza y reconoce por separado (a lo más <see cref="MaxConcurrency"/> a la vez), así nunca están todas las
    /// imágenes en memoria; si una no se puede rasterizar se usa la imagen incrustada en esa página.
    /// </summary>
    public IPdfRasterizer? PdfRasterizer { get; init; } = PdftoppmRasterizer.TryCreate();

    /// <summary>Filas máximas por hoja de cálculo (el resto se resume con un aviso).</summary>
    public int MaxRowsPerSheet { get; init; } = 1000;

    public bool IncludeHiddenSheets { get; init; }

    /// <summary>Procesa las imágenes incrustadas en Word, PowerPoint y PDF (OCR y/o descripción).</summary>
    public bool ProcessEmbeddedImages { get; init; } = true;

    /// <summary>Imágenes incrustadas más pequeñas que esto (bytes) se ignoran (iconos, viñetas...).</summary>
    public int MinEmbeddedImageBytes { get; init; } = 4096;

    /// <summary>Imágenes incrustadas más grandes que esto (bytes, ya descomprimidas) se ignoran (por defecto 20 MB).</summary>
    public int MaxEmbeddedImageBytes { get; init; } = 20 * 1024 * 1024;

    /// <summary>Ruta de <c>ffmpeg</c> (video).</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>
    /// Duración máxima de un video que se analiza con ffmpeg (por defecto 1 hora): se transcribe y se describe solo ese
    /// tramo inicial, con un aviso.
    /// </summary>
    public TimeSpan MaxMediaDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Ejecutor de las herramientas que lanzan los convertidores (ffmpeg); null usa <see cref="ExternalProcess.Runner"/>.
    /// El OCR y el rasterizador reciben el suyo en su constructor.
    /// </summary>
    public IExternalProcessRunner? ProcessRunner { get; init; }

    /// <summary>Intervalo entre fotogramas analizados de un video.</summary>
    public TimeSpan VideoFrameInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxVideoFrames { get; init; } = 30;

    /// <summary>Llamadas de OCR / visión en paralelo.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// Descarga de adjuntos por URL (<see cref="MarkdownConverter.ConvertAsync(ContentPart, bool, CancellationToken)"/> y
    /// <see cref="AttachmentConversionOptions.DownloadUrls"/>): solo http/https, sin redes privadas, con tamaño, tiempo y
    /// redirecciones acotados (ver <see cref="UrlDownloadOptions"/>).
    /// </summary>
    public UrlDownloadOptions UrlDownload { get; init; } = new();

    /// <summary>Recibe el avance de la conversión (p. ej. para una barra de progreso). Se invoca desde varios hilos.</summary>
    public IProgress<DocumentConversionProgress>? Progress { get; init; }
}

/// <summary>Contexto que reciben los convertidores: opciones y servicios de IA compartidos.</summary>
public sealed class DocumentConversionContext(MarkdownConverterOptions options, MarkdownConverter converter)
{
    private int _imageAnalyses;
    private int _imageAnalysesSkipped;

    public MarkdownConverterOptions Options { get; } = options;

    /// <summary>Convertidor principal (para documentos anidados).</summary>
    public MarkdownConverter Converter { get; } = converter;

    public bool CanAnalyzeImages => Options.Ocr is not null || Options.VisionModel is not null;

    /// <summary>Páginas o imágenes que aún se pueden enviar a OCR o visión en este documento (ver <see cref="MarkdownConverterOptions.MaxOcrPages"/>).</summary>
    public int RemainingImageAnalyses => Math.Max(0, Options.MaxOcrPages - Volatile.Read(ref _imageAnalyses));

    /// <summary>Páginas o imágenes que no se analizaron por superar <see cref="MarkdownConverterOptions.MaxOcrPages"/>.</summary>
    public int ImageAnalysesSkipped => Volatile.Read(ref _imageAnalysesSkipped);

    /// <summary>
    /// Reserva hasta <paramref name="count"/> análisis (OCR o visión) del máximo del documento y devuelve cuántos se
    /// concedieron; los demás se cuentan en <see cref="ImageAnalysesSkipped"/>. Es seguro desde varios hilos.
    /// </summary>
    public int ReserveImageAnalyses(int count = 1)
    {
        if (count <= 0)
        {
            return 0;
        }

        while (true)
        {
            var used = Volatile.Read(ref _imageAnalyses);
            var granted = Math.Clamp(Options.MaxOcrPages - used, 0, count);
            if (Interlocked.CompareExchange(ref _imageAnalyses, used + granted, used) == used)
            {
                if (granted < count)
                {
                    Interlocked.Add(ref _imageAnalysesSkipped, count - granted);
                }

                return granted;
            }
        }
    }

    /// <summary>Aviso para los resultados con análisis omitidos por <see cref="MarkdownConverterOptions.MaxOcrPages"/>; null si no hubo.</summary>
    public string? ImageAnalysisLimitWarning() =>
        ImageAnalysesSkipped == 0
            ? null
            : $"{ImageAnalysesSkipped} página(s) o imagen(es) no se enviaron a OCR ni a visión: se alcanzó el máximo del documento (MaxOcrPages = {Options.MaxOcrPages}).";

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

/// <summary>El documento supera <see cref="MarkdownConverterOptions.MaxInputBytes"/> (o el máximo de una descarga) y no se convierte.</summary>
public sealed class DocumentTooLargeException : AiSdkException
{
    public DocumentTooLargeException(long size, long limit, string? fileName = null)
        : base($"El documento{(fileName is null ? string.Empty : $" '{fileName}'")} mide {size} bytes y supera el máximo de {limit} bytes.")
    {
        Size = size;
        Limit = limit;
        FileName = fileName;
    }

    /// <summary>Tamaño del documento en bytes (o lo leído hasta superar el máximo).</summary>
    public long Size { get; }

    /// <summary>Máximo permitido en bytes.</summary>
    public long Limit { get; }

    public string? FileName { get; }
}
