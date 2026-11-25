using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace AiSdk.Documents.Converters;

/// <summary>
/// PDF a Markdown. Extrae el texto en orden de lectura, detecta títulos por tamaño de fuente y quita
/// encabezados y pies de página repetidos. Las páginas escaneadas (sin texto) pasan por OCR: el motor
/// recibe el PDF completo si lo admite (Mistral OCR) o cada página como imagen, una por una (pdftoppm o,
/// si no se puede rasterizar, la imagen incrustada en esa página). Se procesan a lo más
/// <see cref="MarkdownConverterOptions.MaxPdfPages"/> páginas y se reconocen a lo más
/// <see cref="MarkdownConverterOptions.MaxOcrPages"/>, cada una rasterizada sin superar
/// <see cref="MarkdownConverterOptions.MaxRasterPixels"/> por lado.
/// </summary>
public sealed partial class PdfConverter : IDocumentConverter
{
    /// <summary>Resolución con la que se rasterizan las páginas para OCR (menor en páginas muy grandes).</summary>
    public const int OcrDpi = 200;

    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Pdf;

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        var warnings = new List<string>();
        var extracted = new List<(int Number, List<(string Text, double Size)> Blocks)>();
        var sizes = new List<double>();

        // El documento queda abierto hasta terminar el OCR: las imágenes incrustadas se leen solo de las páginas
        // que lo necesitan y solo si no se pudieron rasterizar.
        using var document = PdfDocument.Open(source.Data);
        var title = document.Information.Title is { Length: > 0 } t ? t : null;
        var totalPages = document.NumberOfPages;
        var processed = Math.Clamp(options.MaxPdfPages, 0, totalPages);
        for (var number = 1; number <= processed; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            extracted.Add((page.Number, ExtractBlocks(page)));
            sizes.Add(Math.Max(page.Width, page.Height));
        }

        if (processed < totalPages)
        {
            warnings.Add($"El PDF tiene {totalPages} páginas: solo se procesaron las primeras {processed} (MaxPdfPages).");
        }

        var bodySize = Median(extracted.SelectMany(p => p.Blocks).Where(b => b.Text.Length > 40).Select(b => b.Size))
                       ?? Median(extracted.SelectMany(p => p.Blocks).Select(b => b.Size))
                       ?? 10;
        var repeated = RepeatedEdges(extracted.Select(p => p.Blocks.Select(b => b.Text).ToList()).ToList());

        var pages = new DocumentPage[extracted.Count];
        var needOcr = new List<int>();
        for (var i = 0; i < extracted.Count; i++)
        {
            var (number, blocks) = extracted[i];
            var markdown = Render(blocks.Where(b => !repeated.Contains(Normalize(b.Text))).ToList(), bodySize);
            pages[i] = new DocumentPage(number, markdown);
            var hasText = markdown.Count(char.IsLetterOrDigit) >= options.PdfMinCharsPerPage;
            if (options.PdfOcr == PdfOcrMode.Always || (options.PdfOcr == PdfOcrMode.Auto && !hasText))
            {
                needOcr.Add(i);
            }
        }

        var canOcr = options.Ocr is not null || options.VisionModel is not null;
        if (needOcr.Count > 0 && !canOcr)
        {
            options.Progress?.Report(new DocumentConversionProgress(pages.Length, 0, 0));
            warnings.Add($"{needOcr.Count} página(s) sin texto (¿escaneadas?): configure Ocr o VisionModel para reconocerlas.");
        }
        else if (needOcr.Count > 0)
        {
            // Un motor que lee el PDF completo (p. ej. Mistral OCR) procesa y cobra todas sus páginas: solo se usa si
            // caben en el máximo; si no, se reconocen página por página las primeras que quepan.
            var wholeDocument = options.Ocr is { } pdfOcr && pdfOcr.Supports(MediaTypes.Pdf) && totalPages <= context.RemainingImageAnalyses
                                && context.ReserveImageAnalyses(totalPages) == totalPages;
            if (!wholeDocument)
            {
                needOcr = needOcr.Take(context.ReserveImageAnalyses(needOcr.Count)).ToList();
            }

            options.Progress?.Report(new DocumentConversionProgress(pages.Length, needOcr.Count, 0));
            if (needOcr.Count > 0)
            {
                await OcrPagesAsync(source, context, document, pages, sizes, needOcr, wholeDocument, warnings, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            options.Progress?.Report(new DocumentConversionProgress(pages.Length, 0, 0));
        }

        if (context.ImageAnalysisLimitWarning() is { } limit)
        {
            warnings.Add(limit);
        }

        return DocumentConversionResult.FromPages(source, pages, title, warnings);
    }

    /// <summary>Resolución para rasterizar una página cuyo lado mayor mide <paramref name="points"/> puntos, sin superar <paramref name="maxPixels"/> píxeles.</summary>
    internal static int RasterDpi(double points, int maxPixels) =>
        points <= 0 ? OcrDpi : (int)Math.Clamp(Math.Floor(Math.Max(1, maxPixels) * 72.0 / points), 1, OcrDpi);

    private static async Task OcrPagesAsync(
        DocumentSource source,
        DocumentConversionContext context,
        PdfDocument document,
        DocumentPage[] pages,
        List<double> sizes,
        List<int> needOcr,
        bool wholeDocument,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var options = context.Options;

        // Motores que leen PDF completos (p. ej. Mistral OCR): una sola llamada.
        if (wholeDocument && options.Ocr is { } pdfOcr)
        {
            var result = await pdfOcr.RecognizeAsync(new OcrRequest { Data = source.Data, MediaType = MediaTypes.Pdf, Language = options.Language }, cancellationToken).ConfigureAwait(false);
            foreach (var index in needOcr)
            {
                var number = pages[index].Number;
                if (result.Pages.FirstOrDefault(p => p.Number == number) is { Markdown.Length: > 0 } ocrPage)
                {
                    pages[index] = new DocumentPage(number, ocrPage.Markdown.Trim(), PageTextSource.Ocr);
                }
            }

            options.Progress?.Report(new DocumentConversionProgress(pages.Length, needOcr.Count, needOcr.Count));
            return;
        }

        // Se rasteriza desde un archivo si se conoce el original, o si es pdftoppm (una sola copia temporal para todas
        // las páginas). Otros rasterizadores reciben los bytes en memoria: su RenderFileAsync por defecto releería el
        // archivo completo en cada página.
        var fromFile = options.PdfRasterizer is not null && (IsSameFile(source) || options.PdfRasterizer is PdftoppmRasterizer);
        using var temp = fromFile && !IsSameFile(source) ? new TempDirectory() : null;
        var pdfPath = !fromFile ? null : temp is null ? source.FilePath! : temp.File("in.pdf");
        if (temp is not null)
        {
            await File.WriteAllBytesAsync(pdfPath!, source.Data, cancellationToken).ConfigureAwait(false);
        }

        // Cada página se rasteriza y reconoce dentro del mismo turno: a lo más MaxConcurrency imágenes en memoria.
        var documentLock = new Lock();
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        var missing = 0;
        var notRasterized = 0;
        var done = 0;
        await Task.WhenAll(needOcr.Select(async index =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var number = pages[index].Number;
                byte[]? image = null;
                if (options.PdfRasterizer is { } rasterizer)
                {
                    try
                    {
                        var dpi = RasterDpi(sizes[index], options.MaxRasterPixels);
                        var rendered = pdfPath is not null
                            ? await rasterizer.RenderFileAsync(pdfPath, [number], dpi, cancellationToken).ConfigureAwait(false)
                            : await rasterizer.RenderAsync(source.Data, [number], dpi, cancellationToken).ConfigureAwait(false);
                        image = rendered.GetValueOrDefault(number);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Página dañada o herramienta con problemas: se intenta con la imagen incrustada.
                    }

                    if (image is null)
                    {
                        Interlocked.Increment(ref notRasterized);
                    }
                }

                if (image is null)
                {
                    lock (documentLock)
                    {
                        image = LargestImage(document.GetPage(number), options.MinEmbeddedImageBytes, options.MaxRasterPixels);
                    }
                }

                if (image is null)
                {
                    Interlocked.Increment(ref missing);
                    return;
                }

                var mediaType = MediaTypes.Detect(image);
                string? text;
                var sourceKind = PageTextSource.Ocr;
                if (options.Ocr is not null && options.Ocr.Supports(mediaType))
                {
                    text = (await context.OcrAsync(image, mediaType, cancellationToken).ConfigureAwait(false))?.Markdown;
                }
                else
                {
                    text = await context.DescribeImageAsync(image, mediaType, VisionModelOcr.DefaultInstructions, cancellationToken).ConfigureAwait(false);
                    sourceKind = PageTextSource.Vision;
                }

                if (!string.IsNullOrWhiteSpace(text) && text.Trim() != "(sin texto)")
                {
                    // En modo Always se prefiere el OCR solo si aporta más texto que la extracción directa.
                    if (text.Length >= pages[index].Markdown.Length / 2)
                    {
                        pages[index] = new DocumentPage(number, text.Trim(), sourceKind);
                    }
                }
            }
            finally
            {
                options.Progress?.Report(new DocumentConversionProgress(pages.Length, needOcr.Count, Interlocked.Increment(ref done)));
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (notRasterized > missing)
        {
            warnings.Add($"{notRasterized - missing} página(s) no se pudieron rasterizar: se reconoció la imagen incrustada en la página.");
        }

        if (missing > 0)
        {
            warnings.Add(options.PdfRasterizer is null
                ? $"{missing} página(s) sin texto no se pudieron rasterizar: instale poppler-utils (pdftoppm) o use un OCR que acepte PDF."
                : $"{missing} página(s) sin texto no se pudieron rasterizar ni tienen una imagen incrustada que reconocer.");
        }
    }

    /// <summary>True si <see cref="DocumentSource.FilePath"/> existe y tiene el mismo tamaño que los datos (no cambió).</summary>
    private static bool IsSameFile(DocumentSource source)
    {
        try
        {
            return source.FilePath is { } path && File.Exists(path) && new FileInfo(path).Length == source.Data.LongLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bloques de texto en orden de lectura con su tamaño de fuente. Las líneas iniciales con fuente
    /// distinta del resto del bloque (p. ej. un título pegado al párrafo) se separan en su propio bloque.
    /// </summary>
    private static List<(string Text, double Size)> ExtractBlocks(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        IEnumerable<TextBlock> blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        blocks = UnsupervisedReadingOrderDetector.Instance.Get(blocks.ToList());
        var result = new List<(string, double)>();
        foreach (var block in blocks)
        {
            var lines = block.TextLines
                .Select(l => (Text: l.Text.Trim(), Size: LineSize(l)))
                .Where(l => l.Text.Length > 0)
                .ToList();
            var start = 0;
            for (var i = 1; i <= lines.Count; i++)
            {
                if (i == lines.Count || Math.Abs(lines[i].Size - lines[start].Size) > Math.Max(0.5, lines[start].Size * 0.1))
                {
                    var group = lines.Skip(start).Take(i - start).ToList();
                    result.Add((JoinLines(group.Select(l => l.Text).ToList()), group.Average(l => l.Size)));
                    start = i;
                }
            }
        }

        return result;
    }

    private static double LineSize(TextLine line)
    {
        var letters = line.Words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        return letters.Count == 0 ? 0 : letters.Average(l => l.PointSize);
    }

    /// <summary>
    /// La imagen incrustada más grande de la página (la del escaneo), o null si no hay ninguna utilizable. Las de más de
    /// <paramref name="maxPixels"/>² píxeles se ignoran sin decodificarlas (una imagen comprimida puede ocupar gigabytes).
    /// </summary>
    private static byte[]? LargestImage(Page page, int minBytes, int maxPixels)
    {
        byte[]? largest = null;
        var maxArea = (long)maxPixels * maxPixels;
        foreach (var image in page.GetImages())
        {
            try
            {
                if ((long)image.WidthInSamples * image.HeightInSamples > maxArea)
                {
                    continue;
                }

                var data = image.TryGetPng(out var png) && png.Length >= minBytes ? png : image.RawMemory.ToArray();
                if (data.Length >= minBytes && data.Length > (largest?.Length ?? 0) && MediaTypes.IsImage(MediaTypes.Detect(data)))
                {
                    largest = data;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Imagen con un formato no soportado: se ignora.
            }
        }

        return largest;
    }

    /// <summary>Une las líneas de un bloque en un párrafo, reparando palabras cortadas con guion.</summary>
    private static string JoinLines(List<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0)
            {
                var bullet = BulletRegex().IsMatch(line);
                if (bullet)
                {
                    builder.Append('\n');
                }
                else if (builder[^1] == '-' && builder.Length > 1 && char.IsLetter(builder[^2]))
                {
                    builder.Length--;
                }
                else
                {
                    builder.Append(' ');
                }
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static string Render(List<(string Text, double Size)> blocks, double bodySize)
    {
        var builder = new StringBuilder();
        foreach (var (text, size) in blocks)
        {
            var ratio = bodySize <= 0 ? 1 : size / bodySize;
            var isHeading = ratio >= 1.2 && text.Length <= 150 && !text.Contains('\n', StringComparison.Ordinal);
            if (isHeading)
            {
                builder.Append(ratio >= 1.8 ? "# " : ratio >= 1.4 ? "## " : "### ");
            }
            else if (BulletRegex().IsMatch(text))
            {
                builder.Append(BulletRegex().Replace(text, "- "));
                builder.Append("\n\n");
                continue;
            }

            builder.Append(text).Append("\n\n");
        }

        return builder.ToString().Trim();
    }

    /// <summary>Textos que aparecen al inicio o final de la mayoría de las páginas (encabezados y pies).</summary>
    private static HashSet<string> RepeatedEdges(List<List<string>> pages)
    {
        var result = new HashSet<string>();
        if (pages.Count < 3)
        {
            return result;
        }

        var counts = new Dictionary<string, int>();
        foreach (var blocks in pages)
        {
            foreach (var edge in blocks.Take(2).Concat(blocks.TakeLast(2)).Select(Normalize).Where(e => e.Length is > 0 and < 120).Distinct())
            {
                counts[edge] = counts.GetValueOrDefault(edge) + 1;
            }
        }

        foreach (var (text, count) in counts)
        {
            if (count >= Math.Max(3, pages.Count * 0.6))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static string Normalize(string text) => DigitsRegex().Replace(text.Trim().ToLowerInvariant(), "#");

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).Order().ToList();
        return sorted.Count == 0 ? null : sorted[sorted.Count / 2];
    }

    [GeneratedRegex(@"^\s*[•●▪■◦‣∙·]\s*")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();
}
