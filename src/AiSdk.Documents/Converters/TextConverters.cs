using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Text;

namespace AiSdk.Documents.Converters;

/// <summary>Construcción de tablas Markdown.</summary>
public static class MarkdownTable
{
    /// <summary>Celdas máximas (filas × columnas) de una tabla de <see cref="Build(IReadOnlyList{IReadOnlyList{string}})"/>.</summary>
    public const int DefaultMaxCells = 1_000_000;

    /// <summary>
    /// Tabla con la primera fila como encabezado. Escapa barras verticales y saltos de línea. Todas las filas se rellenan
    /// hasta la fila más ancha, así que se escriben a lo más <see cref="DefaultMaxCells"/> celdas (se omiten filas del
    /// final y, si una sola fila es más ancha, columnas).
    /// </summary>
    public static string Build(IReadOnlyList<IReadOnlyList<string>> rows) => Build(rows, DefaultMaxCells, out _);

    /// <summary>
    /// Igual que <see cref="Build(IReadOnlyList{IReadOnlyList{string}})"/> con a lo más <paramref name="maxCells"/> celdas;
    /// <paramref name="truncated"/> indica si se omitieron filas o columnas. Evita que una fila muy ancha (p. ej. un CSV
    /// con una línea de miles de separadores) multiplique el tamaño de todas las demás.
    /// </summary>
    public static string Build(IReadOnlyList<IReadOnlyList<string>> rows, int maxCells, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(rows);
        truncated = false;
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = rows.Max(r => r.Count);
        if (columns == 0)
        {
            return string.Empty;
        }

        maxCells = Math.Max(2, maxCells);
        if (columns > maxCells / 2)
        {
            columns = maxCells / 2;
            truncated = true;
        }

        var rowLimit = Math.Max(2, maxCells / columns);
        if (rows.Count > rowLimit)
        {
            rows = [.. rows.Take(rowLimit)];
            truncated = true;
        }

        var builder = new StringBuilder();
        void Row(IReadOnlyList<string> cells)
        {
            builder.Append('|');
            for (var i = 0; i < columns; i++)
            {
                builder.Append(' ').Append(i < cells.Count ? Escape(cells[i]) : string.Empty).Append(" |");
            }

            builder.Append('\n');
        }

        Row(rows[0]);
        builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
        foreach (var row in rows.Skip(1))
        {
            Row(row);
        }

        return builder.ToString().TrimEnd('\n');
    }

    public static string Escape(string value) =>
        value.Trim().Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
}

/// <summary>Texto plano, Markdown, CSV/TSV (a tabla), JSON, XML, YAML y código (en bloque de código).</summary>
public sealed class PlainTextConverter : IDocumentConverter
{
    private static readonly Dictionary<string, string> CodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [MediaTypes.Json] = "json",
        ["application/xml"] = "xml",
        ["text/xml"] = "xml",
        ["application/yaml"] = "yaml",
        ["text/yaml"] = "yaml",
        ["application/javascript"] = "javascript",
        ["text/javascript"] = "javascript",
        ["application/x-sh"] = "bash",
        ["text/x-python"] = "python",
        ["text/x-csharp"] = "csharp",
        ["application/sql"] = "sql",
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool CanConvert(string mediaType) => mediaType.StartsWith("text/", StringComparison.Ordinal) && mediaType != MediaTypes.Html
                                                || CodeLanguages.ContainsKey(mediaType);

    public Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var text = source.ReadText().Replace("\r\n", "\n", StringComparison.Ordinal);
        var warnings = new List<string>();
        var markdown = source.MediaType switch
        {
            MediaTypes.Csv => CsvToTable(text, ',', context.Options.MaxRowsPerSheet, warnings),
            "text/tab-separated-values" => CsvToTable(text, '\t', context.Options.MaxRowsPerSheet, warnings),
            MediaTypes.Json => $"```json\n{PrettyJson(text)}\n```",
            _ when CodeLanguages.TryGetValue(source.MediaType, out var language) => $"```{language}\n{text.TrimEnd()}\n```",
            _ => text.Trim(),
        };

        return Task.FromResult(DocumentConversionResult.FromPages(source, [new DocumentPage(1, markdown)], warnings: warnings));
    }

    private static string PrettyJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return text.Trim();
        }
    }

    /// <summary>Columnas máximas que se leen de cada línea de un CSV (como Excel); el resto se omite con un aviso.</summary>
    public const int MaxCsvColumns = 16_384;

    /// <summary>Parser CSV con comillas (RFC 4180) a tabla Markdown. Detecta <c>;</c> como separador.</summary>
    public static string CsvToTable(string text, char separator, int maxRows, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var droppedColumns = false;
        var firstLine = text.AsSpan()[..Math.Max(0, text.IndexOf('\n') is var nl and >= 0 ? nl : text.Length)];
        if (separator == ',' && firstLine.Count(';') > firstLine.Count(','))
        {
            separator = ';';
        }

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var total = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                if (row.Count < MaxCsvColumns)
                {
                    row.Add(cell.ToString());
                }
                else
                {
                    droppedColumns = true;
                }

                cell.Clear();
            }
            else if (c == '\n')
            {
                if (row.Count < MaxCsvColumns)
                {
                    row.Add(cell.ToString());
                }

                cell.Clear();
                if (row.Any(v => v.Length > 0))
                {
                    total++;
                    if (rows.Count <= maxRows)
                    {
                        rows.Add(row);
                    }
                }

                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            if (row.Count < MaxCsvColumns)
            {
                row.Add(cell.ToString());
            }

            if (row.Any(v => v.Length > 0))
            {
                total++;
                if (rows.Count <= maxRows)
                {
                    rows.Add(row);
                }
            }
        }

        if (droppedColumns)
        {
            warnings?.Add($"Se omitieron columnas más allá de la {MaxCsvColumns}.");
        }

        var table = MarkdownTable.Build(rows, MarkdownTable.DefaultMaxCells, out var truncated);
        if (truncated)
        {
            warnings?.Add($"La tabla se recortó a {MarkdownTable.DefaultMaxCells} celdas.");
        }

        if (total > rows.Count)
        {
            var note = $"_… {total - rows.Count} filas más omitidas (de {total - 1})._";
            warnings?.Add($"Se omitieron {total - rows.Count} filas.");
            return table + "\n\n" + note;
        }

        return table;
    }
}

/// <summary>
/// HTML a Markdown (ReverseMarkdown), quitando scripts, estilos y navegación. El HTML se analiza una vez con AngleSharp
/// vigilando el anidamiento; lo que queda por debajo de la profundidad máxima se convierte a texto plano y la
/// conversión corre en un hilo con pila amplia. Así un HTML hostil (miles de <c>&lt;div&gt;</c> anidados, cientos de
/// <c>&lt;script&gt;</c> sin cerrar) ni desborda la pila del proceso ni dispara el tiempo de conversión.
/// </summary>
public sealed class HtmlConverter : IDocumentConverter
{
    /// <summary>Profundidad máxima de <see cref="ToMarkdown(string)"/> (ver <see cref="MarkdownConverterOptions.MaxNestingDepth"/>).</summary>
    public const int DefaultMaxDepth = 128;

    /// <summary>Elementos que no aportan contenido y se descartan con todo su interior.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "head", "nav", "footer", "iframe",
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "img", "input", "keygen", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Elementos cuyo texto no se vuelve a interpretar como HTML; se escriben como <c>&lt;pre&gt;</c>.</summary>
    private static readonly HashSet<string> LiteralText = new(StringComparer.OrdinalIgnoreCase) { "xmp", "plaintext", "noembed", "noframes" };

    public bool CanConvert(string mediaType) => mediaType is MediaTypes.Html or "application/xhtml+xml";

    public Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        var warnings = new List<string>();
        var length = (int)Math.Min(source.Data.LongLength, Math.Max(0, options.MaxHtmlBytes));
        string html;
        using (var reader = new StreamReader(new MemoryStream(source.Data, 0, length), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            html = reader.ReadToEnd();
        }

        if (length < source.Data.Length)
        {
            warnings.Add($"El HTML mide {source.Data.Length} bytes: solo se convirtieron los primeros {length} (MaxHtmlBytes).");
        }

        var (markdown, title) = Convert(html, options.MaxNestingDepth, warnings);
        return Task.FromResult(DocumentConversionResult.FromPages(source, [new DocumentPage(1, markdown)], title, warnings));
    }

    /// <summary>Convierte un fragmento HTML a Markdown (con <see cref="DefaultMaxDepth"/> niveles de anidamiento).</summary>
    public static string ToMarkdown(string html) => ToMarkdown(html, DefaultMaxDepth);

    /// <summary>
    /// Convierte un fragmento HTML a Markdown. Lo anidado a más de <paramref name="maxDepth"/> niveles se convierte a
    /// texto plano y, con un anidamiento extremo, se extrae solo el texto; en ambos casos se agrega un aviso a
    /// <paramref name="warnings"/>.
    /// </summary>
    public static string ToMarkdown(string html, int maxDepth, ICollection<string>? warnings = null) => Convert(html, maxDepth, warnings).Markdown;

    private static (string Markdown, string? Title) Convert(string html, int maxDepth, ICollection<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(html);
        maxDepth = Math.Max(1, maxDepth);
        var (markdown, title, notes) = LargeStack.Run(() => ConvertCore(html, maxDepth));
        foreach (var note in notes)
        {
            warnings?.Add(note);
        }

        return (markdown, title);
    }

    private static (string Markdown, string? Title, List<string> Notes) ConvertCore(string html, int maxDepth)
    {
        var notes = new List<string>();

        // El analizador de HTML es iterativo, pero cada etiqueta cuesta tanto como la profundidad: se corta si el árbol
        // supera un límite duro o tiene demasiados elementos (p. ej. por la reconstrucción de formatos) y se extrae el texto.
        var parseLimit = Math.Max(maxDepth * 4, maxDepth + 64);
        var guard = new ParseGuard(parseLimit, html.Length / 2 + 10_000);
        IDocument document;
        try
        {
            document = new HtmlParser(new HtmlParserOptions { OnCreated = guard.OnCreated }).ParseDocument(html);
        }
        catch (HtmlTooComplexException)
        {
            notes.Add($"El HTML supera {parseLimit} niveles de anidamiento o tiene demasiados elementos: se extrajo solo el texto.");
            return (TextOnly(html), null, notes);
        }

        using (document)
        {
            var title = document.Title?.Trim() is { Length: > 0 } t ? t : null;
            var (body, flattened) = Serialize(document, maxDepth);
            if (flattened)
            {
                notes.Add($"El HTML tiene más de {maxDepth} niveles de anidamiento: lo más profundo se convirtió a texto plano.");
            }

            var config = new ReverseMarkdown.Config { GithubFlavored = true };
            config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
            config.Formatting.RemoveComments = true;
            config.Links.SmartHref = true;
            var markdown = new ReverseMarkdown.Converter(config).Convert(body);
            return (CollapseBlankLines(markdown).Trim(), title, notes);
        }
    }

    /// <summary>
    /// Vuelve a escribir el cuerpo como HTML sin elementos de ruido y con a lo más <paramref name="maxDepth"/> niveles:
    /// el interior de un elemento a esa profundidad se escribe como texto. Recorre el árbol con una pila propia (sin
    /// recursión), porque aún puede ser más profundo que <paramref name="maxDepth"/>.
    /// </summary>
    private static (string Html, bool Flattened) Serialize(IDocument document, int maxDepth)
    {
        var builder = new StringBuilder();
        var flattened = false;
        if (((INode?)document.Body ?? document.DocumentElement) is not { } root)
        {
            return (string.Empty, false);
        }

        var stack = new Stack<(INode Node, int Depth, bool Close)>();
        PushChildren(stack, root, 1);
        while (stack.TryPop(out var item))
        {
            var (node, depth, close) = item;
            if (close)
            {
                builder.Append("</").Append(((IElement)node).LocalName).Append('>');
                continue;
            }

            if (node is IText text)
            {
                AppendEscaped(builder, text.Data);
                continue;
            }

            if (node is not IElement element || Noise.Contains(element.LocalName))
            {
                continue; // comentarios, instrucciones y elementos de ruido
            }

            var name = element.LocalName;
            if (LiteralText.Contains(name))
            {
                builder.Append("<pre>");
                AppendEscaped(builder, TextOf(element));
                builder.Append("</pre>");
                continue;
            }

            builder.Append('<').Append(name);
            foreach (var attribute in element.Attributes)
            {
                if (IsSafeAttributeName(attribute.Name))
                {
                    builder.Append(' ').Append(attribute.Name).Append("=\"");
                    AppendEscaped(builder, attribute.Value);
                    builder.Append('"');
                }
            }

            builder.Append('>');
            if (VoidElements.Contains(name))
            {
                continue;
            }

            if (depth >= maxDepth && element.ChildElementCount > 0)
            {
                flattened = true;
                AppendEscaped(builder, TextOf(element));
                builder.Append("</").Append(name).Append('>');
                continue;
            }

            stack.Push((element, depth, true));
            PushChildren(stack, element, depth + 1);
        }

        return (builder.ToString(), flattened);
    }

    private static void PushChildren(Stack<(INode Node, int Depth, bool Close)> stack, INode node, int depth)
    {
        var children = node.ChildNodes;
        for (var i = children.Length - 1; i >= 0; i--)
        {
            stack.Push((children[i], depth, false));
        }
    }

    /// <summary>Texto de los descendientes (sin ruido), separado por espacios entre elementos; sin recursión.</summary>
    private static string TextOf(INode root)
    {
        var builder = new StringBuilder();
        var stack = new Stack<INode>();
        for (var i = root.ChildNodes.Length - 1; i >= 0; i--)
        {
            stack.Push(root.ChildNodes[i]);
        }

        while (stack.TryPop(out var node))
        {
            if (node is IText text)
            {
                builder.Append(text.Data);
            }
            else if (node is IElement element && !Noise.Contains(element.LocalName))
            {
                builder.Append(' ');
                for (var i = element.ChildNodes.Length - 1; i >= 0; i--)
                {
                    stack.Push(element.ChildNodes[i]);
                }
            }
        }

        return builder.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, string text)
    {
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                ' ' => builder.Append("&nbsp;"),
                _ => builder.Append(c),
            };
        }
    }

    private static bool IsSafeAttributeName(string name) =>
        name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    /// <summary>
    /// Texto de un HTML sin construir el árbol (para HTML con anidamiento extremo): quita etiquetas, comentarios, scripts
    /// y estilos y decodifica las entidades. Un solo recorrido lineal.
    /// </summary>
    internal static string TextOnly(string html)
    {
        var builder = new StringBuilder(html.Length / 2);
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                builder.Append(html, i, html.Length - i);
                break;
            }

            builder.Append(html, i, lt - i);
            var next = lt + 1 < html.Length ? html[lt + 1] : '\0';
            if (!(char.IsAsciiLetter(next) || next is '/' or '!' or '?'))
            {
                builder.Append('<'); // un "<" suelto es texto
                i = lt + 1;
                continue;
            }

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var endComment = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = endComment < 0 ? html.Length : endComment + 3;
                continue;
            }

            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                break; // etiqueta sin cerrar al final
            }

            var nameStart = next == '/' ? lt + 2 : lt + 1;
            var nameEnd = nameStart;
            while (nameEnd < gt && char.IsAsciiLetterOrDigit(html[nameEnd]))
            {
                nameEnd++;
            }

            var name = html[nameStart..nameEnd];
            i = gt + 1;
            if (next != '/' && name.ToLowerInvariant() is "script" or "style" or "noscript" or "template" or "svg" or "iframe" or "textarea" or "title")
            {
                // Contenido sin etiquetas: se salta hasta su cierre.
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                var closeEnd = close < 0 ? -1 : html.IndexOf('>', close);
                i = closeEnd < 0 ? html.Length : closeEnd + 1;
            }

            builder.Append(name.ToLowerInvariant() is "p" or "div" or "br" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "section" or "article" or "table" ? '\n' : ' ');
        }

        var lines = System.Net.WebUtility.HtmlDecode(builder.ToString()).Split('\n')
            .Select(l => string.Join(' ', l.Split([' ', '\t', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)));
        return CollapseBlankLines(string.Join('\n', lines)).Trim();
    }

    /// <summary>Reemplaza cada grupo de líneas en blanco (o solo con espacios) por una sola línea en blanco. Lineal.</summary>
    internal static string CollapseBlankLines(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blank++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(blank > 0 ? "\n\n" : "\n");
            }

            blank = 0;
            builder.Append(line.TrimEnd('\r'));
        }

        return builder.ToString();
    }

    /// <summary>Corta el análisis si el árbol se vuelve demasiado profundo o grande.</summary>
    private sealed class ParseGuard(int maxDepth, int maxElements)
    {
        private IElement? _last;
        private int _count;

        public void OnCreated(IElement element, TextPosition position)
        {
            if (++_count > maxElements)
            {
                throw new HtmlTooComplexException();
            }

            // El elemento anterior ya está en el árbol: se mide su profundidad sin pasar de maxDepth pasos.
            var depth = 0;
            for (var node = _last?.Parent; node is not null; node = node.Parent)
            {
                if (++depth > maxDepth)
                {
                    throw new HtmlTooComplexException();
                }
            }

            _last = element;
        }
    }

    private sealed class HtmlTooComplexException() : Exception("HTML demasiado anidado.");
}

/// <summary>Ejecuta trabajo recursivo de bibliotecas de terceros en un hilo con pila amplia (independiente de la del llamador).</summary>
internal static class LargeStack
{
    private const int StackSize = 16 * 1024 * 1024;

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackSize)
        {
            IsBackground = true,
            Name = "AiSdk.Documents",
        };
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }
}
