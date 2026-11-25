using System.Globalization;
using System.Text;
using System.Text.Json;
using AiSdk.Documents.Resources;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Text;

namespace AiSdk.Documents.Converters;

/// <summary>Building of Markdown tables.</summary>
public static class MarkdownTable
{
    /// <summary>Maximum cells (rows × columns) in a table from <see cref="Build(IReadOnlyList{IReadOnlyList{string}})"/>.</summary>
    public const int DefaultMaxCells = 1_000_000;

    /// <summary>
    /// Table with the first row as the header. Escapes vertical bars and line breaks. All rows are padded to the widest
    /// row, so at most <see cref="DefaultMaxCells"/> cells are written (trailing rows are omitted and, if a single row is
    /// wider, columns as well).
    /// </summary>
    public static string Build(IReadOnlyList<IReadOnlyList<string>> rows) => Build(rows, DefaultMaxCells, out _);

    /// <summary>
    /// Same as <see cref="Build(IReadOnlyList{IReadOnlyList{string}})"/> with at most <paramref name="maxCells"/> cells;
    /// <paramref name="truncated"/> indicates whether rows or columns were omitted. Prevents a very wide row (e.g. a CSV
    /// with a line of thousands of separators) from multiplying the size of all the others.
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

/// <summary>Plain text, Markdown, CSV/TSV (as a table), JSON, XML, YAML and code (in a code block).</summary>
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

    /// <summary>Maximum columns read from each line of a CSV (as in Excel); the rest are skipped with a warning.</summary>
    public const int MaxCsvColumns = 16_384;

    /// <summary>CSV parser with quotes (RFC 4180) to a Markdown table. Detects <c>;</c> as a separator.</summary>
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
            warnings?.Add(string.Format(CultureInfo.CurrentCulture, Strings.Csv_ColumnsOmitted, MaxCsvColumns));
        }

        var table = MarkdownTable.Build(rows, MarkdownTable.DefaultMaxCells, out var truncated);
        if (truncated)
        {
            warnings?.Add(string.Format(CultureInfo.CurrentCulture, Strings.Csv_TableTruncated, MarkdownTable.DefaultMaxCells));
        }

        if (total > rows.Count)
        {
            var note = string.Format(CultureInfo.CurrentCulture, Strings.Csv_RowsOmittedNote, total - rows.Count, total - 1);
            warnings?.Add(string.Format(CultureInfo.CurrentCulture, Strings.Csv_RowsOmitted, total - rows.Count));
            return table + "\n\n" + note;
        }

        return table;
    }
}

/// <summary>
/// HTML to Markdown (ReverseMarkdown), removing scripts, styles and navigation. The HTML is parsed once with AngleSharp
/// while watching the nesting; anything below the maximum depth is converted to plain text and the conversion runs on a
/// thread with a large stack. This way a hostile HTML document (thousands of nested <c>&lt;div&gt;</c> elements,
/// hundreds of unclosed <c>&lt;script&gt;</c> tags) neither overflows the process stack nor blows up the conversion time.
/// </summary>
public sealed class HtmlConverter : IDocumentConverter
{
    /// <summary>Maximum depth for <see cref="ToMarkdown(string)"/> (see <see cref="MarkdownConverterOptions.MaxNestingDepth"/>).</summary>
    public const int DefaultMaxDepth = 128;

    /// <summary>Elements that contribute no content and are discarded along with everything inside them.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "head", "nav", "footer", "iframe",
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "img", "input", "keygen", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Elements whose text is not reinterpreted as HTML; they are written as <c>&lt;pre&gt;</c>.</summary>
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
            warnings.Add(string.Format(CultureInfo.CurrentCulture, Strings.Html_Truncated, source.Data.Length, length));
        }

        var (markdown, title) = Convert(html, options.MaxNestingDepth, warnings);
        return Task.FromResult(DocumentConversionResult.FromPages(source, [new DocumentPage(1, markdown)], title, warnings));
    }

    /// <summary>Converts an HTML fragment to Markdown (with <see cref="DefaultMaxDepth"/> nesting levels).</summary>
    public static string ToMarkdown(string html) => ToMarkdown(html, DefaultMaxDepth);

    /// <summary>
    /// Converts an HTML fragment to Markdown. Content nested more than <paramref name="maxDepth"/> levels deep is
    /// converted to plain text and, with extreme nesting, only the text is extracted; in both cases a warning is added to
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
            notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Html_TooComplex, parseLimit));
            return (TextOnly(html), null, notes);
        }

        using (document)
        {
            var title = document.Title?.Trim() is { Length: > 0 } t ? t : null;
            var (body, flattened) = Serialize(document, maxDepth);
            if (flattened)
            {
                notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.Html_Flattened, maxDepth));
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
    /// Rewrites the body as HTML without noise elements and with at most <paramref name="maxDepth"/> levels: the inside
    /// of an element at that depth is written as text. Walks the tree with its own stack (no recursion), because it may
    /// still be deeper than <paramref name="maxDepth"/>.
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

    /// <summary>Text of the descendants (without noise), separated by spaces between elements; no recursion.</summary>
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
    /// Text of an HTML document without building the tree (for HTML with extreme nesting): removes tags, comments,
    /// scripts and styles and decodes entities. A single linear pass.
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

    /// <summary>Replaces each group of blank lines (or lines with only whitespace) with a single blank line. Linear.</summary>
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

    /// <summary>Stops parsing if the tree becomes too deep or too large.</summary>
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

    private sealed class HtmlTooComplexException() : Exception(Strings.Html_TooNested);
}

/// <summary>Runs recursive work from third-party libraries on a thread with a large stack (independent of the caller's).</summary>
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
