using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using OxImagePart = DocumentFormat.OpenXml.Packaging.ImagePart;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AiSdk.Documents.Converters;

/// <summary>
/// Word (.docx) a Markdown: títulos, listas, negrita/cursiva, enlaces, tablas e imágenes
/// incrustadas (con OCR o descripción si hay un motor configurado).
/// </summary>
public sealed partial class DocxConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Docx;

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(new MemoryStream(source.Data), false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("El documento no tiene contenido principal.");
        var state = new State(main, context);
        var builder = new StringBuilder();
        foreach (var element in main.Document?.Body?.ChildElements ?? [])
        {
            await AppendBlockAsync(element, builder, state, cancellationToken).ConfigureAwait(false);
        }

        var title = document.PackageProperties.Title is { Length: > 0 } t ? t : null;
        var markdown = ExtraBlankLines().Replace(builder.ToString(), "\n\n").Trim();
        return new DocumentConversionResult
        {
            Markdown = markdown,
            MediaType = source.MediaType,
            FileName = source.FileName,
            Title = title,
            Pages = [new DocumentPage(1, markdown, state.UsedAi ? PageTextSource.Vision : PageTextSource.Text)],
            Warnings = state.Warnings,
        };
    }

    private sealed class State(MainDocumentPart main, DocumentConversionContext context)
    {
        public MainDocumentPart Main { get; } = main;

        public DocumentConversionContext Context { get; } = context;

        public List<string> Warnings { get; } = [];

        public bool UsedAi { get; set; }

        public Dictionary<(int NumId, int Level), int> Counters { get; } = [];
    }

    private static async Task AppendBlockAsync(OpenXmlElement element, StringBuilder builder, State state, CancellationToken cancellationToken)
    {
        switch (element)
        {
            case W.Paragraph paragraph:
                var text = await ParagraphAsync(paragraph, state, cancellationToken).ConfigureAwait(false);
                if (text.Length == 0)
                {
                    builder.Append('\n');
                    break;
                }

                var heading = HeadingLevel(paragraph, state.Main);
                var list = ListPrefix(paragraph, state);
                if (heading > 0)
                {
                    builder.Append('\n').Append('#', heading).Append(' ').Append(text.Replace("**", string.Empty, StringComparison.Ordinal)).Append("\n\n");
                }
                else if (list is not null)
                {
                    builder.Append(list).Append(text).Append('\n');
                }
                else
                {
                    builder.Append(text).Append("\n\n");
                }

                break;

            case W.Table table:
                var rows = new List<IReadOnlyList<string>>();
                foreach (var row in table.Elements<W.TableRow>())
                {
                    var cells = new List<string>();
                    foreach (var cell in row.Elements<W.TableCell>())
                    {
                        var parts = new List<string>();
                        foreach (var p in cell.Elements<W.Paragraph>())
                        {
                            var value = await ParagraphAsync(p, state, cancellationToken).ConfigureAwait(false);
                            if (value.Length > 0)
                            {
                                parts.Add(value);
                            }
                        }

                        cells.Add(string.Join("<br>", parts));
                        var span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                        for (var i = 1; i < span; i++)
                        {
                            cells.Add(string.Empty);
                        }
                    }

                    rows.Add(cells);
                }

                builder.Append('\n').Append(MarkdownTable.Build(rows)).Append("\n\n");
                break;

            case W.SdtBlock sdt:
                foreach (var child in sdt.SdtContentBlock?.ChildElements ?? [])
                {
                    await AppendBlockAsync(child, builder, state, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    private static async Task<string> ParagraphAsync(W.Paragraph paragraph, State state, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case W.Run run:
                    builder.Append(await RunAsync(run, state, cancellationToken).ConfigureAwait(false));
                    break;
                case W.Hyperlink link:
                    var label = new StringBuilder();
                    foreach (var run in link.Elements<W.Run>())
                    {
                        label.Append(await RunAsync(run, state, cancellationToken).ConfigureAwait(false));
                    }

                    var target = link.Id?.Value is { } id
                        ? state.Main.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri.ToString()
                        : null;
                    builder.Append(target is null ? label.ToString() : $"[{label}]({target})");
                    break;
                case W.SimpleField or W.SdtRun or W.InsertedRun:
                    foreach (var run in child.Descendants<W.Run>())
                    {
                        builder.Append(await RunAsync(run, state, cancellationToken).ConfigureAwait(false));
                    }

                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static async Task<string> RunAsync(W.Run run, State state, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case W.Text t:
                    text.Append(t.Text);
                    break;
                case W.TabChar:
                    text.Append(' ');
                    break;
                case W.Break or W.CarriageReturn:
                    text.Append("  \n");
                    break;
                case W.Drawing drawing:
                    text.Append(await ImageAsync(drawing, state, cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        var value = text.ToString();
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("\n> ", StringComparison.Ordinal))
        {
            return value;
        }

        var properties = run.RunProperties;
        var bold = properties?.Bold is { } b && (b.Val is null || b.Val.Value);
        var italic = properties?.Italic is { } i && (i.Val is null || i.Val.Value);
        var core = value.Trim();
        var lead = value[..(value.Length - value.TrimStart().Length)];
        var trail = value[value.TrimEnd().Length..];
        if (bold) core = $"**{core}**";
        if (italic) core = $"*{core}*";
        return lead + core + trail;
    }

    private static async Task<string> ImageAsync(W.Drawing drawing, State state, CancellationToken cancellationToken)
    {
        var properties = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault();
        var alt = properties?.Description?.Value ?? properties?.Title?.Value ?? properties?.Name?.Value ?? "imagen";
        var embed = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
        if (embed is null || !state.Context.Options.ProcessEmbeddedImages || !state.Context.CanAnalyzeImages
            || state.Main.GetPartById(embed) is not OxImagePart part)
        {
            return $"![{alt}]";
        }

        using var stream = part.GetStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length < state.Context.Options.MinEmbeddedImageBytes)
        {
            return $"![{alt}]";
        }

        var analysis = await state.Context.AnalyzeImageAsync(buffer.ToArray(), MediaTypes.Normalize(part.ContentType), cancellationToken).ConfigureAwait(false);
        if (analysis is not { Markdown.Length: > 0 } result)
        {
            return $"![{alt}]";
        }

        state.UsedAi = true;
        return $"\n> **[Imagen: {alt}]** {result.Markdown.Replace("\n", "\n> ", StringComparison.Ordinal)}\n";
    }

    private static int HeadingLevel(W.Paragraph paragraph, MainDocumentPart main)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null)
        {
            return 0;
        }

        var style = main.StyleDefinitionsPart?.Styles?.Elements<W.Style>().FirstOrDefault(s => s.StyleId?.Value == styleId);
        var name = style?.StyleName?.Val?.Value ?? styleId;
        if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var match = HeadingName().Match(name);
        if (match.Success)
        {
            return Math.Clamp(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 1, 6);
        }

        var outline = style?.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
        return outline is { } level and < 9 ? Math.Clamp(level + 1, 1, 6) : 0;
    }

    private static string? ListPrefix(W.Paragraph paragraph, State state)
    {
        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        if (numbering?.NumberingId is null && paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } styleId)
        {
            // La numeración puede venir del estilo (p. ej. "List Bullet").
            numbering = state.Main.StyleDefinitionsPart?.Styles?.Elements<W.Style>()
                .FirstOrDefault(s => s.StyleId?.Value == styleId)?.StyleParagraphProperties?.NumberingProperties;
        }

        var numId = numbering?.NumberingId?.Val?.Value;
        if (numId is null or 0)
        {
            return null;
        }

        var level = numbering?.NumberingLevelReference?.Val?.Value ?? 0;
        var indent = new string(' ', level * 2);
        var format = NumberFormat(state.Main, numId.Value, level);
        if (format is null or "bullet")
        {
            return indent + "- ";
        }

        var key = (numId.Value, level);
        state.Counters[key] = state.Counters.GetValueOrDefault(key) + 1;
        foreach (var deeper in state.Counters.Keys.Where(k => k.NumId == numId.Value && k.Level > level).ToList())
        {
            state.Counters.Remove(deeper);
        }

        return $"{indent}{state.Counters[key]}. ";
    }

    private static string? NumberFormat(MainDocumentPart main, int numId, int level)
    {
        var numberingRoot = main.NumberingDefinitionsPart?.Numbering;
        var instance = numberingRoot?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        var abstractNum = numberingRoot?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
        var levelDefinition = abstractNum?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
        return levelDefinition?.NumberingFormat?.Val?.ToString();
    }

    [GeneratedRegex(@"^heading\s*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();
}

/// <summary>Excel (.xlsx) a Markdown: una tabla por hoja, con fechas y fórmulas ya calculadas.</summary>
public sealed class XlsxConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Xlsx;

    public Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(source.Data), false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("El libro no tiene contenido.");
        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        var dateStyles = DateStyleIndexes(workbook);
        var pages = new List<DocumentPage>();
        var warnings = new List<string>();
        var number = 0;

        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hidden = sheet.State?.Value is { } state && state != S.SheetStateValues.Visible;
            if ((hidden && !context.Options.IncludeHiddenSheets) || sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
            {
                continue;
            }

            var grid = new SortedDictionary<int, SortedDictionary<int, string>>();
            foreach (var row in part.Worksheet?.Descendants<S.Row>() ?? [])
            {
                foreach (var cell in row.Elements<S.Cell>())
                {
                    var value = CellValue(cell, sharedStrings, dateStyles);
                    if (string.IsNullOrWhiteSpace(value) || cell.CellReference?.Value is not { } reference)
                    {
                        continue;
                    }

                    var (column, rowIndex) = ParseReference(reference);
                    if (!grid.TryGetValue(rowIndex, out var cells))
                    {
                        grid[rowIndex] = cells = [];
                    }

                    cells[column] = value;
                }
            }

            var name = sheet.Name?.Value ?? $"Hoja {number + 1}";
            number++;
            if (grid.Count == 0)
            {
                continue;
            }

            var firstColumn = grid.Values.Min(r => r.Keys.Min());
            var lastColumn = grid.Values.Max(r => r.Keys.Max());
            var rows = grid.Values
                .Take(context.Options.MaxRowsPerSheet + 1)
                .Select(r => (IReadOnlyList<string>)Enumerable.Range(firstColumn, lastColumn - firstColumn + 1).Select(c => r.GetValueOrDefault(c, string.Empty)).ToList())
                .ToList();
            var markdown = new StringBuilder($"## {name}\n\n").Append(MarkdownTable.Build(rows));
            if (grid.Count > rows.Count)
            {
                markdown.Append(CultureInfo.InvariantCulture, $"\n\n_… {grid.Count - rows.Count} filas más omitidas (total {grid.Count - 1} filas de datos)._");
                warnings.Add($"Hoja '{name}': se omitieron {grid.Count - rows.Count} filas.");
            }

            pages.Add(new DocumentPage(number, markdown.ToString()) { Label = $"Hoja: {name}" });
        }

        var title = document.PackageProperties.Title is { Length: > 0 } t ? t : null;
        return Task.FromResult(DocumentConversionResult.FromPages(source, pages, title, warnings));
    }

    private static string CellValue(S.Cell cell, string[] sharedStrings, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == S.CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index < sharedStrings.Length ? sharedStrings[index] : string.Empty;
        }

        if (type == S.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (type == S.CellValues.Boolean)
        {
            return raw == "1" ? "VERDADERO" : "FALSO";
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if ((type is null || type == S.CellValues.Number)
            && cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2958466)
        {
            var date = DateTime.FromOADate(serial);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? Math.Round(number, 10).ToString("0.##########", CultureInfo.InvariantCulture)
            : raw;
    }

    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbook)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var formats = stylesheet?.CellFormats?.Elements<S.CellFormat>().ToList();
        if (formats is null)
        {
            return result;
        }

        var custom = stylesheet!.NumberingFormats?.Elements<S.NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null)
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? string.Empty) ?? [];
        for (var i = 0; i < formats.Count; i++)
        {
            var id = formats[i].NumberFormatId?.Value ?? 0;
            var isDate = id is >= 14 and <= 22 or >= 45 and <= 47
                         || (custom.TryGetValue(id, out var code) && IsDateFormat(code));
            if (isDate)
            {
                result.Add((uint)i);
            }
        }

        return result;
    }

    private static bool IsDateFormat(string code)
    {
        var withoutLiterals = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", string.Empty);
        return withoutLiterals.IndexOfAny(['d', 'D', 'y', 'Y']) >= 0
               || (withoutLiterals.Contains('m', StringComparison.OrdinalIgnoreCase) && withoutLiterals.Contains('h', StringComparison.OrdinalIgnoreCase));
    }

    private static (int Column, int Row) ParseReference(string reference)
    {
        var column = 0;
        var i = 0;
        for (; i < reference.Length && char.IsLetter(reference[i]); i++)
        {
            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        }

        return (column, int.Parse(reference.AsSpan(i), CultureInfo.InvariantCulture));
    }
}

/// <summary>PowerPoint (.pptx) a Markdown: una sección por diapositiva con título, texto, tablas, imágenes y notas.</summary>
public sealed class PptxConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Pptx;

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        using var document = PresentationDocument.Open(new MemoryStream(source.Data), false);
        var presentation = document.PresentationPart ?? throw new InvalidDataException("La presentación no tiene contenido.");
        var pages = new List<DocumentPage>();
        var number = 0;
        foreach (var slideId in presentation.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            if (slideId.RelationshipId?.Value is not { } rel || presentation.GetPartById(rel) is not SlidePart slide)
            {
                continue;
            }

            number++;
            var usedAi = false;
            string? title = null;
            var body = new StringBuilder();
            foreach (var element in slide.Slide?.CommonSlideData?.ShapeTree?.ChildElements ?? [])
            {
                switch (element)
                {
                    case P.Shape shape:
                        var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
                        var paragraphs = shape.TextBody?.Elements<A.Paragraph>()
                            .Select(p => (Text: string.Concat(p.Descendants<A.Text>().Select(t => t.Text)).Trim(), Level: p.ParagraphProperties?.Level?.Value ?? 0))
                            .Where(p => p.Text.Length > 0)
                            .ToList() ?? [];
                        if (paragraphs.Count == 0)
                        {
                            break;
                        }

                        if (title is null && (placeholder == P.PlaceholderValues.Title || placeholder == P.PlaceholderValues.CenteredTitle))
                        {
                            title = string.Join(' ', paragraphs.Select(p => p.Text));
                        }
                        else if (paragraphs.Count == 1)
                        {
                            body.Append(paragraphs[0].Text).Append("\n\n");
                        }
                        else
                        {
                            foreach (var (text, level) in paragraphs)
                            {
                                body.Append(new string(' ', level * 2)).Append("- ").Append(text).Append('\n');
                            }

                            body.Append('\n');
                        }

                        break;

                    case P.GraphicFrame frame when frame.Descendants<A.Table>().FirstOrDefault() is { } table:
                        var rows = table.Elements<A.TableRow>()
                            .Select(r => (IReadOnlyList<string>)r.Elements<A.TableCell>().Select(c => string.Join("<br>", c.Descendants<A.Paragraph>().Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text))).Where(t => t.Length > 0))).ToList())
                            .ToList();
                        body.Append(MarkdownTable.Build(rows)).Append("\n\n");
                        break;

                    case P.Picture picture when context.Options.ProcessEmbeddedImages && context.CanAnalyzeImages:
                        var embed = picture.BlipFill?.Blip?.Embed?.Value;
                        if (embed is not null && slide.GetPartById(embed) is OxImagePart image)
                        {
                            using var stream = image.GetStream();
                            using var buffer = new MemoryStream();
                            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (buffer.Length >= context.Options.MinEmbeddedImageBytes
                                && await context.AnalyzeImageAsync(buffer.ToArray(), MediaTypes.Normalize(image.ContentType), cancellationToken).ConfigureAwait(false) is { Markdown.Length: > 0 } analysis)
                            {
                                var alt = picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Description?.Value ?? "imagen";
                                body.Append("> **[Imagen: ").Append(alt).Append("]** ").Append(analysis.Markdown.Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");
                                usedAi = true;
                            }
                        }

                        break;
                }
            }

            var notes = slide.NotesSlidePart?.NotesSlide?.Descendants<P.Shape>()
                .Where(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value == P.PlaceholderValues.Body)
                .SelectMany(s => s.Descendants<A.Paragraph>())
                .Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text)).Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (notes is { Count: > 0 })
            {
                body.Append("> **Notas del orador:** ").Append(string.Join(' ', notes)).Append("\n\n");
            }

            var heading = title is null ? $"## Diapositiva {number}" : $"## Diapositiva {number}: {title}";
            pages.Add(new DocumentPage(number, $"{heading}\n\n{body.ToString().Trim()}".Trim(), usedAi ? PageTextSource.Vision : PageTextSource.Text)
            {
                Label = $"Diapositiva {number}",
            });
        }

        var docTitle = document.PackageProperties.Title is { Length: > 0 } t ? t : null;
        return DocumentConversionResult.FromPages(source, pages, docTitle);
    }
}
