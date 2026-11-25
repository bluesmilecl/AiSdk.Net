using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiSdk.Documents.Generation;

/// <summary>Tamaño de página.</summary>
public enum PaperSize
{
    A4,
    Letter,
    Legal,
}

/// <summary>Opciones de generación de Word y PDF.</summary>
public sealed class DocumentStyleOptions
{
    /// <summary>Título del documento (propiedades y, si <see cref="TitlePage"/>, portada).</summary>
    public string? Title { get; init; }

    public string? Subtitle { get; init; }

    public string? Author { get; init; }

    /// <summary>Muestra título, subtítulo, autor y fecha al inicio.</summary>
    public bool TitlePage { get; init; }

    public PaperSize PaperSize { get; init; } = PaperSize.A4;

    public bool Landscape { get; init; }

    /// <summary>Margen en centímetros.</summary>
    public double MarginCm { get; init; } = 2.2;

    /// <summary>Fuente base de Word (en PDF se usa la fuente incrustada o <see cref="PdfFonts"/>).</summary>
    public string FontFamily { get; init; } = "Calibri";

    public double FontSize { get; init; } = 11;

    /// <summary>Color de acento (títulos, encabezados de tabla) en hexadecimal.</summary>
    public string AccentColor { get; init; } = "1F4E79";

    /// <summary>Texto del encabezado de cada página.</summary>
    public string? HeaderText { get; init; }

    /// <summary>Pie de página con "Página n de N".</summary>
    public bool PageNumbers { get; init; } = true;

    /// <summary>Idioma del documento (revisión ortográfica de Word).</summary>
    public string Language { get; init; } = "es-ES";

    /// <summary>Fuentes TrueType para PDF (por defecto se incrustan AiSdk Sans/Mono, que cubren alfabetos latinos y griego).</summary>
    public PdfFonts? PdfFonts { get; init; }
}

/// <summary>Markdown → Word (.docx) con estilos reales (Título 1, Lista, Tabla...), editables en Word.</summary>
public static class DocxWriter
{
    private const int BulletAbstractId = 1;
    private const int DecimalAbstractId = 2;
    private const int BulletNumId = 1;

    public static byte[] FromMarkdown(string markdown, DocumentStyleOptions? options = null) =>
        Write(MarkdownModel.Parse(markdown), options);

    public static byte[] Write(IReadOnlyList<DocBlock> blocks, DocumentStyleOptions? options = null)
    {
        options ??= new DocumentStyleOptions();
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            AddStyles(main, options);
            var numbering = AddNumbering(main);
            var body = new Body();
            main.Document = new Document(body);
            var context = new Context(main, numbering, options);

            if (options.TitlePage && options.Title is not null)
            {
                body.Append(StyledParagraph("Title", options.Title));
                if (options.Subtitle is not null) body.Append(StyledParagraph("Subtitle", options.Subtitle));
                var byline = string.Join(" · ", new[] { options.Author, DateTime.Now.ToString("d 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("es-ES")) }.Where(s => s is not null));
                body.Append(StyledParagraph("Subtitle", byline));
            }

            foreach (var block in blocks)
            {
                AppendBlock(body, block, context, listLevel: 0, quote: false);
            }

            body.Append(SectionProperties(main, options));
            SchemaOrder.Normalize(main.Document);
            SchemaOrder.Normalize(main.StyleDefinitionsPart!.Styles!);
            document.PackageProperties.Title = options.Title;
            document.PackageProperties.Creator = options.Author ?? "AiSdk";
            document.PackageProperties.Created = DateTime.UtcNow;
        }

        return stream.ToArray();
    }

    private sealed class Context(MainDocumentPart main, Numbering numbering, DocumentStyleOptions options)
    {
        public MainDocumentPart Main { get; } = main;

        public Numbering Numbering { get; } = numbering;

        public DocumentStyleOptions Options { get; } = options;

        public int NextNumId { get; set; } = 2;
    }

    private static void AppendBlock(OpenXmlElement parent, DocBlock block, Context context, int listLevel, bool quote)
    {
        switch (block)
        {
            case HeadingBlockModel heading:
                parent.Append(Paragraph($"Heading{Math.Clamp(heading.Level, 1, 6)}", heading.Runs, context));
                break;

            case ParagraphModel paragraph:
                parent.Append(Paragraph(quote ? "Quote" : null, paragraph.Runs, context));
                break;

            case ListModel list:
                var numId = list.Ordered ? NewOrderedList(context, list.Start) : BulletNumId;
                foreach (var item in list.Items)
                {
                    var first = true;
                    foreach (var child in item.Blocks)
                    {
                        if (first && child is ParagraphModel p)
                        {
                            var runs = item.Checked is { } done ? [new TextRun(done ? "☑ " : "☐ "), .. p.Runs] : p.Runs;
                            var paragraph = Paragraph("ListParagraph", runs, context);
                            if (item.Checked is null)
                            {
                                paragraph.ParagraphProperties!.Append(new NumberingProperties(new NumberingLevelReference { Val = listLevel }, new NumberingId { Val = numId }));
                            }
                            else
                            {
                                paragraph.ParagraphProperties!.Append(new Indentation { Left = ((listLevel + 1) * 360).ToString(CultureInfo.InvariantCulture) });
                            }

                            parent.Append(paragraph);
                        }
                        else
                        {
                            AppendBlock(parent, child, context, listLevel + 1, quote);
                        }

                        first = false;
                    }
                }

                break;

            case CodeBlockModel code:
                var codeParagraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Code" }));
                var lines = code.Code.Split('\n');
                var run = new Run();
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) run.Append(new Break());
                    run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
                }

                codeParagraph.Append(run);
                parent.Append(codeParagraph);
                break;

            case QuoteModel q:
                foreach (var child in q.Blocks)
                {
                    AppendBlock(parent, child, context, listLevel, quote: true);
                }

                break;

            case TableModel table:
                parent.Append(Table(table, context));
                parent.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" })));
                break;

            case RuleModel:
                parent.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
                    new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "BFBFBF" }))));
                break;

            case PageBreakModel:
                parent.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                break;
        }
    }

    private static Paragraph StyledParagraph(string style, string text) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Paragraph(string? style, IReadOnlyList<TextRun> runs, Context context, JustificationValues? align = null)
    {
        var properties = new ParagraphProperties();
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        if (align is { } a) properties.Append(new Justification { Val = a });
        var paragraph = new Paragraph(properties);
        foreach (var run in runs)
        {
            var element = Run(run);
            if (MarkdownModel.IsSafeLink(run.Link, out var uri))
            {
                var rel = context.Main.AddHyperlinkRelationship(uri, true);
                element.RunProperties!.Append(new RunStyle { Val = "Hyperlink" });
                paragraph.Append(new Hyperlink(element) { Id = rel.Id, History = true });
            }
            else
            {
                paragraph.Append(element);
            }
        }

        return paragraph;
    }

    private static Run Run(TextRun run)
    {
        var properties = new RunProperties();
        if (run.Bold) properties.Append(new Bold());
        if (run.Italic) properties.Append(new Italic());
        if (run.Strike) properties.Append(new Strike());
        if (run.Code)
        {
            properties.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
            properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" });
        }

        var element = new Run(properties);
        var parts = run.Text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) element.Append(new Break());
            element.Append(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return element;
    }

    private static Table Table(TableModel model, Context context)
    {
        var columns = model.Rows.Count == 0 ? 0 : model.Rows.Max(r => r.Count);
        var table = new Table(new TableProperties(
            new TableStyle { Val = "AiSdkTable" },
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableLook { Val = "04A0" }));
        var grid = new TableGrid();
        for (var c = 0; c < columns; c++) grid.Append(new GridColumn());
        table.Append(grid);

        for (var r = 0; r < model.Rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.Append(new TableRowProperties(new TableHeader()));
            for (var c = 0; c < columns; c++)
            {
                var runs = c < model.Rows[r].Count ? model.Rows[r][c] : [];
                if (r == 0) runs = runs.Select(x => x with { Bold = true }).ToList();
                var align = (c < model.Align.Count ? model.Align[c] : CellAlign.Left) switch
                {
                    CellAlign.Center => JustificationValues.Center,
                    CellAlign.Right => JustificationValues.Right,
                    _ => JustificationValues.Left,
                };
                var paragraph = Paragraph("TableText", runs, context, align);
                var cellProperties = new TableCellProperties(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                if (r == 0) cellProperties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = context.Options.AccentColor });
                if (r == 0) paragraph.Descendants<RunProperties>().ToList().ForEach(p => p.Append(new Color { Val = "FFFFFF" }));
                row.Append(new TableCell(cellProperties, paragraph));
            }

            table.Append(row);
        }

        return table;
    }

    private static int NewOrderedList(Context context, int start)
    {
        var id = context.NextNumId++;
        var instance = new NumberingInstance(new AbstractNumId { Val = DecimalAbstractId }) { NumberID = id };
        instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = start }) { LevelIndex = 0 });
        context.Numbering.Append(instance);
        return id;
    }

    private static Numbering AddNumbering(MainDocumentPart main)
    {
        var bullets = new AbstractNum { AbstractNumberId = BulletAbstractId };
        var decimals = new AbstractNum { AbstractNumberId = DecimalAbstractId };
        string[] symbols = ["•", "◦", "▪"];
        for (var level = 0; level < 9; level++)
        {
            var indent = (level + 1) * 360;
            bullets.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = symbols[level % 3] },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = indent.ToString(CultureInfo.InvariantCulture), Hanging = "360" }))
            { LevelIndex = level });
            decimals.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = level % 3 == 1 ? NumberFormatValues.LowerLetter : level % 3 == 2 ? NumberFormatValues.LowerRoman : NumberFormatValues.Decimal },
                new LevelText { Val = $"%{level + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = indent.ToString(CultureInfo.InvariantCulture), Hanging = "360" }))
            { LevelIndex = level });
        }

        var numbering = new Numbering(bullets, decimals, new NumberingInstance(new AbstractNumId { Val = BulletAbstractId }) { NumberID = BulletNumId });
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = numbering;
        return numbering;
    }

    private static void AddStyles(MainDocumentPart main, DocumentStyleOptions options)
    {
        var size = ((int)Math.Round(options.FontSize * 2)).ToString(CultureInfo.InvariantCulture);
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = options.FontFamily, HighAnsi = options.FontFamily, ComplexScript = options.FontFamily, EastAsia = options.FontFamily },
                    new FontSize { Val = size },
                    new FontSizeComplexScript { Val = size },
                    new Languages { Val = options.Language })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        styles.Append(ParagraphStyle("Title", "Title", 52, options.AccentColor, bold: true, before: 0, after: 120));
        styles.Append(ParagraphStyle("Subtitle", "Subtitle", 26, "595959", bold: false, before: 0, after: 360));
        int[] sizes = [36, 30, 26, 24, 22, 22];
        for (var level = 1; level <= 6; level++)
        {
            var style = ParagraphStyle($"Heading{level}", $"heading {level}", sizes[level - 1], options.AccentColor, bold: true, before: level == 1 ? 360 : 240, after: 120);
            style.StyleParagraphProperties!.Append(new KeepNext(), new OutlineLevel { Val = level - 1 });
            styles.Append(style);
        }

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "60" }, new ContextualSpacing()))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });
        styles.Append(new Style(
            new StyleName { Val = "Table Text" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(new SpacingBetweenLines { Before = "40", After = "40" }))
        { Type = StyleValues.Paragraph, StyleId = "TableText" });
        styles.Append(new Style(
            new StyleName { Val = "Quote" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Indentation { Left = "567" },
                new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18, Space = 8, Color = "BFBFBF" })),
            new StyleRunProperties(new Italic(), new Color { Val = "595959" }))
        { Type = StyleValues.Paragraph, StyleId = "Quote" });
        styles.Append(new Style(
            new StyleName { Val = "Code" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "F5F5F5" },
                new SpacingBetweenLines { After = "120", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new FontSize { Val = "19" }))
        { Type = StyleValues.Paragraph, StyleId = "Code" });
        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" },
            new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });
        styles.Append(new Style(
            new StyleName { Val = "AiSdk Table" },
            new StyleTableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "AiSdkTable" });
        styles.Append(new Style(new StyleName { Val = "Footer" }, new BasedOn { Val = "Normal" },
            new StyleRunProperties(new FontSize { Val = "18" }, new Color { Val = "7F7F7F" }))
        { Type = StyleValues.Paragraph, StyleId = "Footer" });

        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
    }

    private static Style ParagraphStyle(string id, string name, int halfPoints, string color, bool bold, int before, int after)
    {
        var run = new StyleRunProperties(new Color { Val = color }, new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
        if (bold) run.Append(new Bold());
        return new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { Before = before.ToString(CultureInfo.InvariantCulture), After = after.ToString(CultureInfo.InvariantCulture) }),
            run)
        { Type = StyleValues.Paragraph, StyleId = id };
    }

    private static SectionProperties SectionProperties(MainDocumentPart main, DocumentStyleOptions options)
    {
        var (width, height) = options.PaperSize switch
        {
            PaperSize.Letter => (12240u, 15840u),
            PaperSize.Legal => (12240u, 20160u),
            _ => (11906u, 16838u),
        };
        if (options.Landscape) (width, height) = (height, width);
        var margin = (int)Math.Round(options.MarginCm * 567);
        var section = new SectionProperties();

        if (options.HeaderText is not null)
        {
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Footer" }, new Justification { Val = JustificationValues.Right }),
                new Run(new Text(options.HeaderText))));
            section.Append(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) });
        }

        if (options.PageNumbers)
        {
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Footer" }, new Justification { Val = JustificationValues.Center }),
                new Run(new Text("Página ") { Space = SpaceProcessingModeValues.Preserve }),
                new SimpleField(new Run(new Text("1"))) { Instruction = " PAGE " },
                new Run(new Text(" de ") { Space = SpaceProcessingModeValues.Preserve }),
                new SimpleField(new Run(new Text("1"))) { Instruction = " NUMPAGES " }));
            section.Append(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) });
        }

        section.Append(
            new PageSize { Width = width, Height = height, Orient = options.Landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
            new PageMargin { Top = margin, Bottom = margin, Left = (uint)margin, Right = (uint)margin, Header = 567, Footer = 567 });
        return section;
    }
}

/// <summary>Ordena los hijos de las propiedades según el esquema de WordprocessingML (Word es estricto con el orden).</summary>
internal static class SchemaOrder
{
    private static readonly string[] RunOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow", "emboss", "imprint",
        "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz", "szCs", "highlight", "u",
        "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath",
    ];

    private static readonly string[] ParagraphOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers", "pBdr", "shd",
        "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi",
        "adjustRightInd", "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection",
        "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr", "sectPr", "pPrChange",
    ];

    private static readonly string[] CellOrder =
    [
        "cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar", "textDirection", "tcFitText", "vAlign", "hideMark",
    ];

    public static void Normalize(OpenXmlElement root)
    {
        foreach (var element in root.Descendants().ToList())
        {
            var order = element switch
            {
                RunProperties or StyleRunProperties or ParagraphMarkRunProperties => RunOrder,
                ParagraphProperties or StyleParagraphProperties => ParagraphOrder,
                TableCellProperties => CellOrder,
                _ => null,
            };
            if (order is null || element.ChildElements.Count < 2)
            {
                continue;
            }

            var children = element.ChildElements.ToList();
            var sorted = children.OrderBy(c => Array.IndexOf(order, c.LocalName) is var i and >= 0 ? i : int.MaxValue).ToList();
            if (!children.SequenceEqual(sorted))
            {
                element.RemoveAllChildren();
                foreach (var child in sorted)
                {
                    element.AppendChild(child);
                }
            }
        }
    }
}
