using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using OxImagePart = DocumentFormat.OpenXml.Packaging.ImagePart;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AiSdk.Documents.Converters;

/// <summary>
/// Lectura acotada de documentos Office. El SDK de Open XML carga cada parte de forma recursiva, así que antes de
/// cargarla se recorre su XML en flujo (sin DTD) comprobando la profundidad y el tamaño: un archivo de pocos KB con
/// miles de elementos anidados desbordaría la pila del proceso.
/// </summary>
internal static class OfficeXml
{
    /// <summary>Nivel máximo de una lista en Word y PowerPoint (0 a 8).</summary>
    public const int MaxListLevel = 8;

    public static OpenSettings Settings(MarkdownConverterOptions options) => new() { MaxCharactersInPart = options.MaxOfficePartChars };

    public static XmlReaderSettings ReaderSettings(MarkdownConverterOptions options) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = options.MaxOfficePartChars,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    /// <summary>Raíz de la parte (p. ej. <c>p =&gt; p.Document</c>) después de comprobar su XML; null si no hay parte.</summary>
    public static TRoot? Load<TPart, TRoot>(TPart? part, Func<TPart, TRoot?> root, MarkdownConverterOptions options)
        where TPart : OpenXmlPart
    {
        if (part is null)
        {
            return default;
        }

        Check(part, options);
        return root(part);
    }

    /// <summary>Lanza <see cref="InvalidDataException"/> si la parte supera <see cref="MarkdownConverterOptions.MaxNestingDepth"/> o <see cref="MarkdownConverterOptions.MaxOfficePartChars"/>.</summary>
    public static void Check(OpenXmlPart part, MarkdownConverterOptions options)
    {
        try
        {
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            using var reader = XmlReader.Create(stream, ReaderSettings(options));
            while (reader.Read())
            {
                CheckDepth(reader, part, options);
            }
        }
        catch (XmlException ex)
        {
            throw Unreadable(part, options, ex);
        }
    }

    public static void CheckDepth(XmlReader reader, OpenXmlPart part, MarkdownConverterOptions options)
    {
        if (reader.Depth > options.MaxNestingDepth)
        {
            throw new InvalidDataException($"La parte '{part.Uri}' tiene más de {options.MaxNestingDepth} niveles de anidamiento (MaxNestingDepth).");
        }
    }

    public static InvalidDataException Unreadable(OpenXmlPart part, MarkdownConverterOptions options, XmlException ex) =>
        new($"La parte '{part.Uri}' no es XML válido o supera {options.MaxOfficePartChars} caracteres (MaxOfficePartChars): {ex.Message}", ex);

    /// <summary>
    /// Bytes de una imagen incrustada para analizarla, o null si es más chica que <see cref="MarkdownConverterOptions.MinEmbeddedImageBytes"/>,
    /// más grande que <see cref="MarkdownConverterOptions.MaxEmbeddedImageBytes"/> (se deja de leer al superarlo) o ya
    /// no quedan análisis en el presupuesto del documento (<see cref="MarkdownConverterOptions.MaxOcrPages"/>).
    /// </summary>
    public static async Task<byte[]?> ReadImageAsync(OpenXmlPart part, DocumentConversionContext context, CancellationToken cancellationToken)
    {
        var options = context.Options;
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxEmbeddedImageBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length < options.MinEmbeddedImageBytes || context.ReserveImageAnalyses() == 0 ? null : buffer.ToArray();
    }

    public static void AddLimitWarnings(List<string> warnings, DocumentConversionContext context, bool tableTruncated)
    {
        if (tableTruncated)
        {
            warnings.Add($"Una o más tablas se recortaron (máximo {MarkdownTable.DefaultMaxCells} celdas por tabla).");
        }

        if (context.ImageAnalysisLimitWarning() is { } images)
        {
            warnings.Add(images);
        }

        if (context.OversizedImageWarning() is { } oversized)
        {
            warnings.Add(oversized);
        }
    }
}

/// <summary>
/// Word (.docx) a Markdown: títulos, listas, negrita/cursiva, enlaces, tablas e imágenes
/// incrustadas (con OCR o descripción si hay un motor configurado).
/// </summary>
public sealed partial class DocxConverter : IDocumentConverter
{
    /// <summary>Columnas máximas de una tabla de Word (también acota <c>gridSpan</c>).</summary>
    public const int MaxTableColumns = 63;

    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Docx;

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        using var document = WordprocessingDocument.Open(new MemoryStream(source.Data), false, OfficeXml.Settings(options));
        var main = document.MainDocumentPart ?? throw new InvalidDataException("El documento no tiene contenido principal.");
        var body = OfficeXml.Load(main, p => p.Document, options)?.Body;
        var state = new State(main, context);
        var builder = new StringBuilder();
        foreach (var element in body?.ChildElements ?? [])
        {
            await AppendBlockAsync(element, builder, state, cancellationToken).ConfigureAwait(false);
        }

        if (state.ColumnsTruncated)
        {
            state.Warnings.Add($"Una o más tablas tienen más de {MaxTableColumns} columnas: se omitieron las demás.");
        }

        OfficeXml.AddLimitWarnings(state.Warnings, context, state.TableTruncated);
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

    /// <summary>Estado de la conversión, con índices de estilos, numeraciones y enlaces (búsquedas O(1) por párrafo).</summary>
    private sealed class State
    {
        public State(MainDocumentPart main, DocumentConversionContext context)
        {
            Main = main;
            Context = context;
            foreach (var style in OfficeXml.Load(main.StyleDefinitionsPart, p => p.Styles, context.Options)?.Elements<W.Style>() ?? [])
            {
                if (style.StyleId?.Value is { } id)
                {
                    Styles.TryAdd(id, style);
                }
            }

            var numbering = OfficeXml.Load(main.NumberingDefinitionsPart, p => p.Numbering, context.Options);
            foreach (var instance in numbering?.Elements<W.NumberingInstance>() ?? [])
            {
                if (instance.NumberID?.Value is { } numId && instance.AbstractNumId?.Val?.Value is { } abstractId)
                {
                    AbstractByNumId.TryAdd(numId, abstractId);
                }
            }

            foreach (var abstractNum in numbering?.Elements<W.AbstractNum>() ?? [])
            {
                if (abstractNum.AbstractNumberId?.Value is { } id)
                {
                    AbstractNums.TryAdd(id, abstractNum);
                }
            }

            foreach (var relationship in main.HyperlinkRelationships)
            {
                Hyperlinks.TryAdd(relationship.Id, relationship.Uri.ToString());
            }
        }

        public MainDocumentPart Main { get; }

        public DocumentConversionContext Context { get; }

        public List<string> Warnings { get; } = [];

        public bool UsedAi { get; set; }

        public bool ColumnsTruncated { get; set; }

        public bool TableTruncated { get; set; }

        public Dictionary<string, W.Style> Styles { get; } = new(StringComparer.Ordinal);

        public Dictionary<int, int> AbstractByNumId { get; } = [];

        public Dictionary<int, W.AbstractNum> AbstractNums { get; } = [];

        public Dictionary<(int NumId, int Level), string?> NumberFormats { get; } = [];

        public Dictionary<string, string> Hyperlinks { get; } = new(StringComparer.Ordinal);

        /// <summary>Contador de cada nivel (0 a 8) por numeración.</summary>
        public Dictionary<int, int[]> Counters { get; } = [];
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

                var heading = HeadingLevel(paragraph, state);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var cells = new List<string>();
                    foreach (var cell in row.Elements<W.TableCell>())
                    {
                        if (cells.Count >= MaxTableColumns)
                        {
                            state.ColumnsTruncated = true;
                            break;
                        }

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

                        // Una celda combinada ocupa gridSpan columnas; Word admite a lo más 63.
                        var span = Math.Clamp(cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1, 1, MaxTableColumns);
                        for (var i = 1; i < span && cells.Count < MaxTableColumns; i++)
                        {
                            cells.Add(string.Empty);
                        }
                    }

                    rows.Add(cells);
                }

                builder.Append('\n').Append(MarkdownTable.Build(rows, MarkdownTable.DefaultMaxCells, out var truncated)).Append("\n\n");
                state.TableTruncated |= truncated;
                break;

            case W.SdtBlock sdt:
                // La profundidad de los controles anidados está acotada por MaxNestingDepth (se comprobó al cargar).
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

                    var target = link.Id?.Value is { } id ? state.Hyperlinks.GetValueOrDefault(id) : null;
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
            || !state.Main.TryGetPartById(embed, out var found) || found is not OxImagePart part
            || await OfficeXml.ReadImageAsync(part, state.Context, cancellationToken).ConfigureAwait(false) is not { } image)
        {
            return $"![{alt}]";
        }

        var analysis = await state.Context.AnalyzeImageAsync(image, MediaTypes.Normalize(part.ContentType), cancellationToken).ConfigureAwait(false);
        if (analysis is not { Markdown.Length: > 0 } result)
        {
            return $"![{alt}]";
        }

        state.UsedAi = true;
        return $"\n> **[Imagen: {alt}]** {result.Markdown.Replace("\n", "\n> ", StringComparison.Ordinal)}\n";
    }

    private static int HeadingLevel(W.Paragraph paragraph, State state)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null)
        {
            return 0;
        }

        var style = state.Styles.GetValueOrDefault(styleId);
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
        return outline is { } level and >= 0 and < 9 ? Math.Clamp(level + 1, 1, 6) : 0;
    }

    private static string? ListPrefix(W.Paragraph paragraph, State state)
    {
        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        if (numbering?.NumberingId is null && paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } styleId)
        {
            // La numeración puede venir del estilo (p. ej. "List Bullet").
            numbering = state.Styles.GetValueOrDefault(styleId)?.StyleParagraphProperties?.NumberingProperties;
        }

        var numId = numbering?.NumberingId?.Val?.Value;
        if (numId is null or 0)
        {
            return null;
        }

        // Word usa los niveles 0 a 8; un nivel mayor no puede pedir una sangría gigante.
        var level = Math.Clamp(numbering?.NumberingLevelReference?.Val?.Value ?? 0, 0, OfficeXml.MaxListLevel);
        var indent = new string(' ', level * 2);
        var format = NumberFormat(state, numId.Value, level);
        if (format is null or "bullet")
        {
            return indent + "- ";
        }

        if (!state.Counters.TryGetValue(numId.Value, out var counters))
        {
            state.Counters[numId.Value] = counters = new int[OfficeXml.MaxListLevel + 1];
        }

        counters[level]++;
        Array.Clear(counters, level + 1, counters.Length - level - 1);
        return $"{indent}{counters[level]}. ";
    }

    private static string? NumberFormat(State state, int numId, int level)
    {
        if (!state.NumberFormats.TryGetValue((numId, level), out var format))
        {
            var abstractNum = state.AbstractByNumId.TryGetValue(numId, out var abstractId) ? state.AbstractNums.GetValueOrDefault(abstractId) : null;
            var levelDefinition = abstractNum?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
            state.NumberFormats[(numId, level)] = format = levelDefinition?.NumberingFormat?.Val?.ToString();
        }

        return format;
    }

    [GeneratedRegex(@"^heading\s*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();
}

/// <summary>
/// Excel (.xlsx) a Markdown: una tabla por hoja, con fechas y fórmulas ya calculadas. Las hojas se leen en flujo y solo
/// se guardan las primeras <see cref="MarkdownConverterOptions.MaxRowsPerSheet"/> filas, así que una hoja enorme (o una
/// celda en la columna ZZZZZZ) no ocupa más memoria.
/// </summary>
public sealed class XlsxConverter : IDocumentConverter
{
    /// <summary>Columnas de una hoja de Excel (A a XFD).</summary>
    public const int MaxColumns = 16_384;

    /// <summary>Filas de una hoja de Excel.</summary>
    public const int MaxRows = 1_048_576;

    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Xlsx;

    public Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        using var document = SpreadsheetDocument.Open(new MemoryStream(source.Data), false, OfficeXml.Settings(options));
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("El libro no tiene contenido.");
        var sharedStrings = ReadSharedStrings(workbook.SharedStringTablePart, options);
        var dateStyles = DateStyleIndexes(workbook, options);
        var pages = new List<DocumentPage>();
        var warnings = new List<string>();
        var number = 0;
        var tableTruncated = false;

        foreach (var sheet in OfficeXml.Load(workbook, p => p.Workbook, options)?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hidden = sheet.State?.Value is { } state && state != S.SheetStateValues.Visible;
            if ((hidden && !options.IncludeHiddenSheets) || sheet.Id?.Value is not { } id
                || !workbook.TryGetPartById(id, out var found) || found is not WorksheetPart part)
            {
                continue;
            }

            var name = sheet.Name?.Value ?? $"Hoja {number + 1}";
            number++;
            var sheetData = ReadSheet(part, sharedStrings, dateStyles, options);
            if (sheetData.Rows.Count == 0)
            {
                continue;
            }

            // Solo las columnas con algún valor: celdas dispersas (A1 y XFD2) no generan miles de columnas vacías.
            var columns = sheetData.Rows.Values.SelectMany(r => r.Keys).Distinct().Order().ToList();
            var rows = sheetData.Rows.Values
                .Select(r => (IReadOnlyList<string>)columns.Select(c => r.GetValueOrDefault(c, string.Empty)).ToList())
                .ToList();
            var markdown = new StringBuilder($"## {name}\n\n").Append(MarkdownTable.Build(rows, MarkdownTable.DefaultMaxCells, out var truncated));
            tableTruncated |= truncated;
            var omitted = sheetData.TotalRows - rows.Count;
            if (omitted > 0)
            {
                var more = sheetData.Complete ? string.Empty : "al menos ";
                markdown.Append(CultureInfo.InvariantCulture, $"\n\n_… {more}{omitted} filas más omitidas (total {more}{sheetData.TotalRows - 1} filas de datos)._");
                warnings.Add($"Hoja '{name}': se omitieron {more}{omitted} filas.");
            }

            pages.Add(new DocumentPage(number, markdown.ToString()) { Label = $"Hoja: {name}" });
        }

        OfficeXml.AddLimitWarnings(warnings, context, tableTruncated);
        var title = document.PackageProperties.Title is { Length: > 0 } t ? t : null;
        return Task.FromResult(DocumentConversionResult.FromPages(source, pages, title, warnings));
    }

    /// <summary>Filas guardadas (las primeras), filas con datos contadas y si se pudo contar la hoja completa.</summary>
    private sealed record SheetData(SortedDictionary<int, SortedDictionary<int, string>> Rows, int TotalRows, bool Complete);

    /// <summary>
    /// Lee la hoja en flujo. Guarda las <see cref="MarkdownConverterOptions.MaxRowsPerSheet"/> + 1 primeras filas con
    /// datos (encabezado incluido) y del resto solo cuenta cuántas hay, hasta <see cref="MarkdownConverterOptions.MaxOfficePartChars"/>.
    /// </summary>
    private static SheetData ReadSheet(WorksheetPart part, string[] sharedStrings, HashSet<uint> dateStyles, MarkdownConverterOptions options)
    {
        var keep = Math.Max(1, options.MaxRowsPerSheet + 1);
        var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
        var keys = new SortedSet<int>();
        var total = 0;
        var lastCounted = -1;
        var rowIndex = 0;
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, OfficeXml.ReaderSettings(options));
        try
        {
            while (reader.Read())
            {
                OfficeXml.CheckDepth(reader, part, options);
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                {
                    continue;
                }

                rowIndex = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : rowIndex + 1;
                var cells = ReadRow(reader, part, sharedStrings, dateStyles, options);
                if (cells.Count == 0 || rowIndex is < 1 or > MaxRows)
                {
                    continue;
                }

                if (rowIndex != lastCounted)
                {
                    total++;
                    lastCounted = rowIndex;
                }

                if (rows.TryGetValue(rowIndex, out var existing))
                {
                    foreach (var (column, value) in cells)
                    {
                        existing[column] = value;
                    }
                }
                else if (rows.Count < keep || rowIndex < keys.Max)
                {
                    // Las filas deberían venir en orden; si no, se conservan igual las de menor número.
                    rows[rowIndex] = cells;
                    keys.Add(rowIndex);
                    if (rows.Count > keep)
                    {
                        rows.Remove(keys.Max);
                        keys.Remove(keys.Max);
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            if (rows.Count < keep)
            {
                throw OfficeXml.Unreadable(part, options, ex);
            }

            // Ya se tienen las filas que se muestran: el resto de la hoja (demasiado grande o dañado) solo se contaba.
            return new SheetData(rows, Math.Max(total, rows.Count), Complete: false);
        }

        return new SheetData(rows, total, Complete: true);
    }

    /// <summary>Celdas con valor de la fila actual (el lector queda al final de la fila).</summary>
    private static SortedDictionary<int, string> ReadRow(XmlReader reader, OpenXmlPart part, string[] sharedStrings, HashSet<uint> dateStyles, MarkdownConverterOptions options)
    {
        var cells = new SortedDictionary<int, string>();
        if (reader.IsEmptyElement)
        {
            return cells;
        }

        var rowDepth = reader.Depth;
        var column = 0;
        while (reader.Read() && reader.Depth > rowDepth)
        {
            OfficeXml.CheckDepth(reader, part, options);
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c" || reader.Depth != rowDepth + 1)
            {
                continue;
            }

            var reference = reader.GetAttribute("r");
            column = reference is null ? column + 1 : ParseColumn(reference);
            var type = reader.GetAttribute("t");
            var style = uint.TryParse(reader.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : (uint?)null;
            string? raw = null;
            string? inline = null;
            if (!reader.IsEmptyElement)
            {
                var cellDepth = reader.Depth;
                while (reader.Read() && reader.Depth > cellDepth)
                {
                    OfficeXml.CheckDepth(reader, part, options);
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == cellDepth + 1)
                    {
                        if (reader.LocalName == "v")
                        {
                            raw = ReadText(reader, part, options, richText: false);
                        }
                        else if (reader.LocalName == "is")
                        {
                            inline = ReadText(reader, part, options, richText: true);
                        }
                    }
                }
            }

            if (column is < 1 or > MaxColumns)
            {
                continue; // referencia inválida o fuera de Excel (p. ej. ZZZZZZ1): se ignora la celda
            }

            var value = CellValue(type, style, raw, inline, sharedStrings, dateStyles);
            if (!string.IsNullOrWhiteSpace(value))
            {
                cells[column] = value;
            }
        }

        return cells;
    }

    /// <summary>
    /// Texto del elemento actual (el lector queda en su cierre). Con <paramref name="richText"/>, solo el de los
    /// elementos <c>t</c>, sin las guías fonéticas (<c>rPh</c>).
    /// </summary>
    private static string ReadText(XmlReader reader, OpenXmlPart part, MarkdownConverterOptions options, bool richText)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var start = reader.Depth;
        var builder = new StringBuilder();
        var textDepth = -1;
        var phoneticDepth = -1;
        while (reader.Read() && reader.Depth > start)
        {
            OfficeXml.CheckDepth(reader, part, options);
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when !reader.IsEmptyElement && reader.LocalName == "t" && textDepth < 0:
                    textDepth = reader.Depth;
                    break;
                case XmlNodeType.Element when !reader.IsEmptyElement && reader.LocalName == "rPh" && phoneticDepth < 0:
                    phoneticDepth = reader.Depth;
                    break;
                case XmlNodeType.EndElement when reader.Depth == textDepth:
                    textDepth = -1;
                    break;
                case XmlNodeType.EndElement when reader.Depth == phoneticDepth:
                    phoneticDepth = -1;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace
                    when !richText || (textDepth >= 0 && phoneticDepth < 0):
                    builder.Append(reader.Value);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string[] ReadSharedStrings(SharedStringTablePart? part, MarkdownConverterOptions options)
    {
        if (part is null)
        {
            return [];
        }

        var items = new List<string>();
        try
        {
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            using var reader = XmlReader.Create(stream, OfficeXml.ReaderSettings(options));
            while (reader.Read())
            {
                OfficeXml.CheckDepth(reader, part, options);
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si" && reader.Depth == 1)
                {
                    items.Add(ReadText(reader, part, options, richText: true));
                }
            }
        }
        catch (XmlException ex)
        {
            throw OfficeXml.Unreadable(part, options, ex);
        }

        return [.. items];
    }

    private static string CellValue(string? type, uint? style, string? raw, string? inline, string[] sharedStrings, HashSet<uint> dateStyles)
    {
        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Length ? sharedStrings[index] : string.Empty;
            case "inlineStr":
                return inline ?? string.Empty;
            case "b":
                return raw?.Trim() == "1" ? "VERDADERO" : "FALSO";
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if (type is null or "n" && style is { } s && dateStyles.Contains(s)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2958466)
        {
            var date = DateTime.FromOADate(serial);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? Math.Round(number, 10).ToString("0.##########", CultureInfo.InvariantCulture)
            : raw;
    }

    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbook, MarkdownConverterOptions options)
    {
        var result = new HashSet<uint>();
        var stylesheet = OfficeXml.Load(workbook.WorkbookStylesPart, p => p.Stylesheet, options);
        var formats = stylesheet?.CellFormats?.Elements<S.CellFormat>().ToList();
        if (formats is null)
        {
            return result;
        }

        var custom = new Dictionary<uint, string>();
        foreach (var format in stylesheet!.NumberingFormats?.Elements<S.NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } formatId)
            {
                custom.TryAdd(formatId, format.FormatCode?.Value ?? string.Empty);
            }
        }

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
        var withoutLiterals = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));
        return withoutLiterals.IndexOfAny(['d', 'D', 'y', 'Y']) >= 0
               || (withoutLiterals.Contains('m', StringComparison.OrdinalIgnoreCase) && withoutLiterals.Contains('h', StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Columna (1 a 16.384) de una referencia como <c>B7</c>; 0 si no es válida (más de 3 letras, fuera de XFD o sin fila).</summary>
    internal static int ParseColumn(string reference)
    {
        var column = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
        {
            if (i == 3)
            {
                return 0;
            }

            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        }

        return i > 0 && column <= MaxColumns && i < reference.Length && char.IsAsciiDigit(reference[i]) ? column : 0;
    }
}

/// <summary>PowerPoint (.pptx) a Markdown: una sección por diapositiva con título, texto, tablas, imágenes y notas.</summary>
public sealed class PptxConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => mediaType == MediaTypes.Pptx;

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        using var document = PresentationDocument.Open(new MemoryStream(source.Data), false, OfficeXml.Settings(options));
        var presentation = document.PresentationPart ?? throw new InvalidDataException("La presentación no tiene contenido.");
        var pages = new List<DocumentPage>();
        var warnings = new List<string>();
        var tableTruncated = false;
        var number = 0;
        foreach (var slideId in OfficeXml.Load(presentation, p => p.Presentation, options)?.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slideId.RelationshipId?.Value is not { } rel || !presentation.TryGetPartById(rel, out var found) || found is not SlidePart slide)
            {
                continue;
            }

            number++;
            var usedAi = false;
            string? title = null;
            var body = new StringBuilder();
            foreach (var element in OfficeXml.Load(slide, p => p.Slide, options)?.CommonSlideData?.ShapeTree?.ChildElements ?? [])
            {
                switch (element)
                {
                    case P.Shape shape:
                        var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
                        var paragraphs = shape.TextBody?.Elements<A.Paragraph>()
                            .Select(p => (Text: string.Concat(p.Descendants<A.Text>().Select(t => t.Text)).Trim(), Level: Math.Clamp(p.ParagraphProperties?.Level?.Value ?? 0, 0, OfficeXml.MaxListLevel)))
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
                                body.Append(' ', level * 2).Append("- ").Append(text).Append('\n');
                            }

                            body.Append('\n');
                        }

                        break;

                    case P.GraphicFrame frame when frame.Descendants<A.Table>().FirstOrDefault() is { } table:
                        var rows = table.Elements<A.TableRow>()
                            .Select(r => (IReadOnlyList<string>)r.Elements<A.TableCell>().Select(c => string.Join("<br>", c.Descendants<A.Paragraph>().Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text))).Where(t => t.Length > 0))).ToList())
                            .ToList();
                        body.Append(MarkdownTable.Build(rows, MarkdownTable.DefaultMaxCells, out var truncated)).Append("\n\n");
                        tableTruncated |= truncated;
                        break;

                    case P.Picture picture when options.ProcessEmbeddedImages && context.CanAnalyzeImages:
                        var embed = picture.BlipFill?.Blip?.Embed?.Value;
                        if (embed is not null && slide.TryGetPartById(embed, out var imagePart) && imagePart is OxImagePart image
                            && await OfficeXml.ReadImageAsync(image, context, cancellationToken).ConfigureAwait(false) is { } data
                            && await context.AnalyzeImageAsync(data, MediaTypes.Normalize(image.ContentType), cancellationToken).ConfigureAwait(false) is { Markdown.Length: > 0 } analysis)
                        {
                            var alt = picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Description?.Value ?? "imagen";
                            body.Append("> **[Imagen: ").Append(alt).Append("]** ").Append(analysis.Markdown.Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");
                            usedAi = true;
                        }

                        break;
                }
            }

            var notes = OfficeXml.Load(slide.NotesSlidePart, p => p.NotesSlide, options)?.Descendants<P.Shape>()
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

        OfficeXml.AddLimitWarnings(warnings, context, tableTruncated);
        var docTitle = document.PackageProperties.Title is { Length: > 0 } t ? t : null;
        return DocumentConversionResult.FromPages(source, pages, docTitle, warnings);
    }
}
