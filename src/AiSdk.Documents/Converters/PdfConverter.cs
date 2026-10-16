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
/// recibe el PDF completo si lo admite (Mistral OCR) o cada página como imagen (pdftoppm o la imagen
/// incrustada de la página).
/// </summary>
public sealed partial class PdfConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Pdf;

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        var warnings = new List<string>();
        string? title;
        var extracted = new List<(int Number, List<(string Text, double Size)> Blocks, List<byte[]> Images)>();

        using (var document = PdfDocument.Open(source.Data))
        {
            title = document.Information.Title is { Length: > 0 } t ? t : null;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                extracted.Add((page.Number, ExtractBlocks(page), ExtractImages(page, options.MinEmbeddedImageBytes)));
            }
        }

        var bodySize = Median(extracted.SelectMany(p => p.Blocks).Where(b => b.Text.Length > 40).Select(b => b.Size))
                       ?? Median(extracted.SelectMany(p => p.Blocks).Select(b => b.Size))
                       ?? 10;
        var repeated = RepeatedEdges(extracted.Select(p => p.Blocks.Select(b => b.Text).ToList()).ToList());

        var pages = new DocumentPage[extracted.Count];
        var needOcr = new List<int>();
        for (var i = 0; i < extracted.Count; i++)
        {
            var (number, blocks, _) = extracted[i];
            var markdown = Render(blocks.Where(b => !repeated.Contains(Normalize(b.Text))).ToList(), bodySize);
            pages[i] = new DocumentPage(number, markdown);
            var hasText = markdown.Count(char.IsLetterOrDigit) >= options.PdfMinCharsPerPage;
            if (options.PdfOcr == PdfOcrMode.Always || (options.PdfOcr == PdfOcrMode.Auto && !hasText))
            {
                needOcr.Add(i);
            }
        }

        if (needOcr.Count > 0)
        {
            if (options.Ocr is null && options.VisionModel is null)
            {
                warnings.Add($"{needOcr.Count} página(s) sin texto (¿escaneadas?): configure Ocr o VisionModel para reconocerlas.");
            }
            else
            {
                await OcrPagesAsync(source, context, extracted, pages, needOcr, warnings, cancellationToken).ConfigureAwait(false);
            }
        }

        return DocumentConversionResult.FromPages(source, pages, title, warnings);
    }

    private static async Task OcrPagesAsync(
        DocumentSource source,
        DocumentConversionContext context,
        List<(int Number, List<(string Text, double Size)> Blocks, List<byte[]> Images)> extracted,
        DocumentPage[] pages,
        List<int> needOcr,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var options = context.Options;

        // Motores que leen PDF completos (p. ej. Mistral OCR): una sola llamada.
        if (options.Ocr is { } pdfOcr && pdfOcr.Supports(MediaTypes.Pdf))
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

            return;
        }

        IReadOnlyDictionary<int, byte[]> rendered = new Dictionary<int, byte[]>();
        if (options.PdfRasterizer is { } rasterizer)
        {
            rendered = await rasterizer.RenderAsync(source.Data, needOcr.Select(i => pages[i].Number).ToList(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        var missing = 0;
        await Task.WhenAll(needOcr.Select(async index =>
        {
            var number = pages[index].Number;
            byte[]? image = rendered.GetValueOrDefault(number) ?? extracted[index].Images.OrderByDescending(i => i.Length).FirstOrDefault();
            if (image is null)
            {
                Interlocked.Increment(ref missing);
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
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
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (missing > 0)
        {
            warnings.Add($"{missing} página(s) sin texto no se pudieron rasterizar: instale poppler-utils (pdftoppm) o use un OCR que acepte PDF.");
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

    private static List<byte[]> ExtractImages(Page page, int minBytes)
    {
        var images = new List<byte[]>();
        foreach (var image in page.GetImages())
        {
            try
            {
                if (image.TryGetPng(out var png) && png.Length >= minBytes)
                {
                    images.Add(png);
                    continue;
                }

                var raw = image.RawMemory.ToArray();
                if (raw.Length >= minBytes && MediaTypes.IsImage(MediaTypes.Detect(raw)))
                {
                    images.Add(raw);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Imagen con un formato no soportado: se ignora.
            }
        }

        return images;
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
