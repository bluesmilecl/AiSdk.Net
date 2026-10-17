using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AiSdk.Documents.Generation;

/// <summary>Tipo de una columna de hoja de cálculo.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ColumnTypeJsonConverter))]
public enum ColumnType
{
    /// <summary>Se deduce de cada valor.</summary>
    Auto,
    Text,
    Number,

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Tipo de columna de hoja de cálculo; coincide con el valor que envían los modelos (\"integer\").")]
    Integer,
    Currency,
    Percent,
    Date,
    DateTime,
    Boolean,
}

/// <summary>Serializa <see cref="ColumnType"/> en camelCase (<c>"currency"</c>, <c>"dateTime"</c>).</summary>
public sealed class ColumnTypeJsonConverter() : System.Text.Json.Serialization.JsonStringEnumConverter<ColumnType>(JsonNamingPolicy.CamelCase);

/// <summary>Columna: encabezado, tipo, formato Excel opcional (p. ej. <c>"#,##0.00 €"</c>) y ancho.</summary>
public sealed record SheetColumn(string Header, ColumnType Type = ColumnType.Auto, string? Format = null, double? Width = null);

/// <summary>Opciones de una hoja.</summary>
public sealed record SheetOptions
{
    /// <summary>Agrega una fila de totales con <c>SUM</c> en las columnas numéricas.</summary>
    public bool Totals { get; init; }

    public bool AutoFilter { get; init; } = true;

    /// <summary>Deja fija la fila de encabezados al desplazarse.</summary>
    public bool FreezeHeader { get; init; } = true;
}

/// <summary>
/// Crea archivos Excel (.xlsx) con formato profesional: encabezados con estilo, tipos y formatos por
/// columna (moneda, porcentaje, fechas), filtros, panel fijo, anchos automáticos y fila de totales.
/// Los valores de texto que empiezan con <c>=</c> se escriben como fórmulas.
/// </summary>
/// <example>
/// <code>
/// var xlsx = new SpreadsheetBuilder()
///     .AddSheet("Ventas", ventas, new SheetOptions { Totals = true })   // lista de objetos
///     .Build();
/// </code>
/// </example>
public sealed class SpreadsheetBuilder
{
    private readonly List<(string Name, IReadOnlyList<SheetColumn> Columns, List<IReadOnlyList<object?>> Rows, SheetOptions Options)> _sheets = [];

    /// <summary>Color de acento del encabezado (hexadecimal).</summary>
    public string AccentColor { get; init; } = "1F4E79";

    /// <summary>Formato de moneda por defecto.</summary>
    public string CurrencyFormat { get; init; } = "\"$\"#,##0.00";

    public string DateFormat { get; init; } = "dd/mm/yyyy";

    public string? Title { get; init; }

    public string? Author { get; init; }

    public SpreadsheetBuilder AddSheet(string name, IReadOnlyList<SheetColumn> columns, IEnumerable<IReadOnlyList<object?>> rows, SheetOptions? options = null)
    {
        _sheets.Add((SafeSheetName(name), columns, rows.ToList(), options ?? new SheetOptions()));
        return this;
    }

    /// <summary>Hoja a partir de objetos: una columna por propiedad pública (usa <c>[DisplayName]</c> o <c>[Description]</c> como encabezado).</summary>
    public SpreadsheetBuilder AddSheet<T>(string name, IEnumerable<T> items, SheetOptions? options = null)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToList();
        var columns = properties.Select(p => new SheetColumn(
            p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? p.Name,
            TypeOf(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))).ToList();
        var rows = items.Select(item => (IReadOnlyList<object?>)properties.Select(p => p.GetValue(item)).ToList());
        return AddSheet(name, columns, rows, options);
    }

    /// <summary>Una hoja por cada tabla Markdown del texto (el encabezado de la sección precedente da el nombre).</summary>
    public SpreadsheetBuilder AddMarkdownTables(string markdown, SheetOptions? options = null)
    {
        string? lastHeading = null;
        var index = 0;
        foreach (var block in MarkdownModel.Parse(markdown))
        {
            if (block is HeadingBlockModel heading)
            {
                lastHeading = string.Concat(heading.Runs.Select(r => r.Text));
            }
            else if (block is TableModel table && table.Rows.Count > 0)
            {
                index++;
                var header = table.Rows[0].Select(c => string.Concat(c.Select(r => r.Text))).ToList();
                var rows = table.Rows.Skip(1).Select(r => (IReadOnlyList<object?>)r.Select(c => (object?)string.Concat(c.Select(x => x.Text))).ToList());
                AddSheet(lastHeading ?? $"Tabla {index}", header.Select(h => new SheetColumn(h)).ToList(), rows, options);
            }
        }

        return this;
    }

    public byte[] Build()
    {
        if (_sheets.Count == 0)
        {
            throw new InvalidOperationException("Agregue al menos una hoja.");
        }

        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            var styles = new StyleRegistry(AccentColor);
            var sheets = new Sheets();
            uint sheetId = 1;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawName, columns, rows, options) in _sheets)
            {
                var name = rawName;
                for (var n = 2; !usedNames.Add(name); n++) name = $"{rawName[..Math.Min(rawName.Length, 27)]} ({n})";
                var part = workbookPart.AddNewPart<WorksheetPart>();
                part.Worksheet = BuildSheet(columns, rows, options, styles);
                sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = sheetId++, Name = name });
            }

            workbookPart.Workbook = new Workbook(sheets, new CalculationProperties { FullCalculationOnLoad = true });
            workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = styles.Build();
            document.PackageProperties.Title = Title;
            document.PackageProperties.Creator = Author ?? "AiSdk";
            document.PackageProperties.Created = DateTime.UtcNow;
        }

        return stream.ToArray();
    }

    private Worksheet BuildSheet(IReadOnlyList<SheetColumn> columns, List<IReadOnlyList<object?>> rows, SheetOptions options, StyleRegistry styles)
    {
        var count = Math.Max(columns.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        var cols = Enumerable.Range(0, count).Select(i => i < columns.Count ? columns[i] : new SheetColumn($"Columna {i + 1}")).ToList();
        var types = cols.Select((c, i) => c.Type != ColumnType.Auto ? c.Type : Infer(rows.Select(r => i < r.Count ? Normalize(r[i]) : null))).ToList();
        var formats = cols.Select((c, i) => c.Format ?? DefaultFormat(types[i])).ToList();

        var data = new SheetData();
        var header = new Row { RowIndex = 1 };
        for (var c = 0; c < count; c++)
        {
            header.Append(TextCell(Reference(c, 1), cols[c].Header, styles.Header));
        }

        data.Append(header);
        var widths = cols.Select(c => (double)Math.Max(8, c.Header.Length + 2)).ToArray();
        var sums = new double[count];
        var hasFormula = new bool[count];

        for (var r = 0; r < rows.Count; r++)
        {
            var rowIndex = (uint)(r + 2);
            var row = new Row { RowIndex = rowIndex };
            for (var c = 0; c < count; c++)
            {
                var value = c < rows[r].Count ? Normalize(rows[r][c]) : null;
                var (cell, display, number) = ValueCell(Reference(c, rowIndex), value, types[c], formats[c], styles);
                if (cell is not null) row.Append(cell);
                if (number is { } n) sums[c] += n;
                hasFormula[c] |= value is string f && f.StartsWith('=');
                widths[c] = Math.Max(widths[c], Math.Min(60, display.Length * 1.1 + 2));
            }

            data.Append(row);
        }

        var lastRow = (uint)(rows.Count + 1);
        if (options.Totals && rows.Count > 0)
        {
            var totalRow = new Row { RowIndex = lastRow + 1 };
            for (var c = 0; c < count; c++)
            {
                var reference = Reference(c, lastRow + 1);
                if (types[c] is ColumnType.Number or ColumnType.Integer or ColumnType.Currency)
                {
                    totalRow.Append(new Cell
                    {
                        CellReference = reference,
                        CellFormula = new CellFormula($"SUM({Reference(c, 2)}:{Reference(c, lastRow)})"),
                        // Sin valor en caché si la columna tiene fórmulas: Excel lo calcula al abrir.
                        CellValue = hasFormula[c] ? null : new CellValue(sums[c].ToString(CultureInfo.InvariantCulture)),
                        StyleIndex = styles.Get(formats[c], bold: true, topBorder: true),
                    });
                }
                else
                {
                    totalRow.Append(TextCell(reference, c == 0 ? "Total" : string.Empty, styles.Get(null, bold: true, topBorder: true)));
                }
            }

            data.Append(totalRow);
        }

        var worksheet = new Worksheet();
        if (options.FreezeHeader)
        {
            worksheet.Append(new SheetViews(new SheetView(
                new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
                new Selection { Pane = PaneValues.BottomLeft })
            { WorkbookViewId = 0, TabSelected = _sheets.Count == 1 }));
        }

        var columnsElement = new Columns();
        for (var c = 0; c < count; c++)
        {
            var width = cols[c].Width ?? widths[c] + (types[c] is ColumnType.Currency ? 3 : 0);
            columnsElement.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = Math.Round(width, 1), CustomWidth = true });
        }

        if (count > 0) worksheet.Append(columnsElement);
        worksheet.Append(data);
        if (options.AutoFilter && count > 0 && rows.Count > 0)
        {
            worksheet.Append(new AutoFilter { Reference = $"A1:{Reference(count - 1, lastRow)}" });
        }

        return worksheet;
    }

    private static object? Normalize(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.GetRawText(),
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset d => d.DateTime,
        _ => value,
    };

    private (Cell? Cell, string Display, double? Number) ValueCell(string reference, object? value, ColumnType type, string? format, StyleRegistry styles)
    {
        switch (value)
        {
            case null:
                return (null, string.Empty, null);
            case string s when s.StartsWith('=') && s.Length > 1:
                return (new Cell { CellReference = reference, CellFormula = new CellFormula(s[1..]), StyleIndex = styles.Get(format) }, s, null);
            case bool b:
                return (new Cell { CellReference = reference, DataType = CellValues.Boolean, CellValue = new CellValue(b ? "1" : "0"), StyleIndex = styles.Get(null) }, b ? "TRUE" : "FALSE", null);
            case DateTime date:
                return (NumberCell(reference, date.ToOADate(), styles.Get(format ?? DateFormat)), (format ?? DateFormat), null);
        }

        if (type is ColumnType.Date or ColumnType.DateTime && value is string ds
            && DateTime.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate))
        {
            return (NumberCell(reference, parsedDate.ToOADate(), styles.Get(format)), format ?? DateFormat, null);
        }

        if (type is not ColumnType.Text && TryNumber(value, type, out var number))
        {
            var display = number.ToString("#,##0.##", CultureInfo.InvariantCulture);
            return (NumberCell(reference, number, styles.Get(format)), display, number);
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return (TextCell(reference, text, styles.Get(null)), text, null);
    }

    private static bool TryNumber(object value, ColumnType type, out double number)
    {
        switch (value)
        {
            case double d: number = d; return true;
            case float f: number = f; return true;
            case decimal m: number = (double)m; return true;
            case int or long or short or byte or uint or ulong: number = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
            case string s:
                var clean = s.Trim().Replace("$", string.Empty, StringComparison.Ordinal).Replace("€", string.Empty, StringComparison.Ordinal).Trim();
                var percent = clean.EndsWith('%');
                if (percent) clean = clean[..^1].Trim();
                if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !clean.Contains(',', StringComparison.Ordinal))
                {
                    if (percent || (type == ColumnType.Percent && number > 1)) number /= 100;
                    return true;
                }

                // Formato latino: 1.234,56
                if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"^-?\d{1,3}(\.\d{3})*(,\d+)?$|^-?\d+,\d+$")
                    && double.TryParse(clean.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    if (percent || (type == ColumnType.Percent && number > 1)) number /= 100;
                    return true;
                }

                break;
        }

        number = 0;
        return false;
    }

    private static ColumnType Infer(IEnumerable<object?> values)
    {
        var sample = values.Where(v => v is not null && !(v is string s && string.IsNullOrWhiteSpace(s))).Take(200).ToList();
        if (sample.Count == 0) return ColumnType.Text;
        if (sample.All(v => v is DateTime)) return ColumnType.Date;
        if (sample.All(v => v is bool)) return ColumnType.Boolean;
        if (sample.All(v => v is string s && s.TrimEnd().EndsWith('%') && TryNumber(s, ColumnType.Percent, out _))) return ColumnType.Percent;
        if (sample.All(v => v is string s && s.Contains('$', StringComparison.Ordinal) && TryNumber(s, ColumnType.Currency, out _))) return ColumnType.Currency;
        if (sample.All(v => v is not string { } s || !s.StartsWith('0') || s.Length == 1 || s.StartsWith("0.", StringComparison.Ordinal) || s.StartsWith("0,", StringComparison.Ordinal))
            && sample.All(v => v is not bool && TryNumber(v!, ColumnType.Number, out _)))
        {
            return sample.All(v => TryNumber(v!, ColumnType.Number, out var n) && n == Math.Floor(n) && Math.Abs(n) < 1e15) ? ColumnType.Integer : ColumnType.Number;
        }

        return ColumnType.Text;
    }

    private static ColumnType TypeOf(Type type) => type switch
    {
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) => ColumnType.Date,
        _ when type == typeof(bool) => ColumnType.Boolean,
        _ when type == typeof(int) || type == typeof(long) || type == typeof(short) => ColumnType.Integer,
        _ when type == typeof(decimal) => ColumnType.Currency,
        _ when type == typeof(double) || type == typeof(float) => ColumnType.Number,
        _ => ColumnType.Text,
    };

    private string? DefaultFormat(ColumnType type) => type switch
    {
        ColumnType.Integer => "#,##0",
        ColumnType.Number => "#,##0.00",
        ColumnType.Currency => CurrencyFormat,
        ColumnType.Percent => "0.0%",
        ColumnType.Date => DateFormat,
        ColumnType.DateTime => DateFormat + " hh:mm",
        _ => null,
    };

    private static Cell NumberCell(string reference, double value, uint style) =>
        new() { CellReference = reference, CellValue = new CellValue(value.ToString("R", CultureInfo.InvariantCulture)), StyleIndex = style };

    private static Cell TextCell(string reference, string text, uint style) =>
        new(new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve })) { CellReference = reference, DataType = CellValues.InlineString, StyleIndex = style };

    private static string Reference(int column, uint row)
    {
        var name = string.Empty;
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
        {
            name = (char)('A' + (c - 1) % 26) + name;
        }

        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    private static string SafeSheetName(string name)
    {
        var clean = new string(name.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim('\'').Trim();
        return clean.Length == 0 ? "Hoja" : clean[..Math.Min(31, clean.Length)];
    }

    /// <summary>Registra combinaciones de formato numérico / negrita / borde y genera la hoja de estilos.</summary>
    private sealed class StyleRegistry(string accent)
    {
        private readonly Dictionary<string, uint> _numberFormats = [];
        private readonly List<(uint NumFmt, bool Bold, bool TopBorder)> _formats = [];
        private readonly Dictionary<(uint, bool, bool), uint> _index = [];

        public uint Header { get; } = 1; // índice 1 reservado para el encabezado

        public uint Get(string? format, bool bold = false, bool topBorder = false)
        {
            var numFmt = format is null ? 0u : NumberFormatId(format);
            if (_index.TryGetValue((numFmt, bold, topBorder), out var index))
            {
                return index;
            }

            index = (uint)(_formats.Count + 2);
            _formats.Add((numFmt, bold, topBorder));
            _index[(numFmt, bold, topBorder)] = index;
            return index;
        }

        private uint NumberFormatId(string format)
        {
            switch (format)
            {
                case "0": return 1;
                case "0.00": return 2;
                case "#,##0": return 3;
                case "#,##0.00": return 4;
                case "0%": return 9;
                case "0.00%": return 10;
            }

            if (!_numberFormats.TryGetValue(format, out var id))
            {
                id = (uint)(164 + _numberFormats.Count);
                _numberFormats[format] = id;
            }

            return id;
        }

        public Stylesheet Build()
        {
            var numberingFormats = new NumberingFormats(_numberFormats.Select(kv => new NumberingFormat { NumberFormatId = kv.Value, FormatCode = kv.Key }));
            numberingFormats.Count = (uint)_numberFormats.Count;
            var fonts = new Fonts(
                new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Calibri" }))
            { Count = 3 };
            var fills = new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FF" + accent }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid }))
            { Count = 3 };
            var borders = new Borders(
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(new Color { Rgb = "FF808080" }) { Style = BorderStyleValues.Thin }, new BottomBorder(), new DiagonalBorder()))
            { Count = 2 };
            var cellFormats = new CellFormats(
                new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 },
                new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = true })
                { FontId = 2, FillId = 2, BorderId = 0, NumberFormatId = 0, ApplyFont = true, ApplyFill = true, ApplyAlignment = true });
            foreach (var (numFmt, bold, topBorder) in _formats)
            {
                cellFormats.Append(new CellFormat
                {
                    NumberFormatId = numFmt,
                    FontId = bold ? 1u : 0u,
                    FillId = 0,
                    BorderId = topBorder ? 1u : 0u,
                    ApplyNumberFormat = numFmt != 0,
                    ApplyFont = bold,
                    ApplyBorder = topBorder,
                });
            }

            cellFormats.Count = (uint)cellFormats.ChildElements.Count;
            var stylesheet = new Stylesheet();
            if (_numberFormats.Count > 0) stylesheet.Append(numberingFormats);
            stylesheet.Append(fonts, fills, borders, new CellStyleFormats(new CellFormat()) { Count = 1 }, cellFormats);
            return stylesheet;
        }
    }
}
