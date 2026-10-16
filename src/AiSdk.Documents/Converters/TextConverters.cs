using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSdk.Documents.Converters;

/// <summary>Construcción de tablas Markdown.</summary>
public static class MarkdownTable
{
    /// <summary>Tabla con la primera fila como encabezado. Escapa barras verticales y saltos de línea.</summary>
    public static string Build(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = rows.Max(r => r.Count);
        if (columns == 0)
        {
            return string.Empty;
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
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException)
        {
            return text.Trim();
        }
    }

    /// <summary>Parser CSV con comillas (RFC 4180) a tabla Markdown. Detecta <c>;</c> como separador.</summary>
    public static string CsvToTable(string text, char separator, int maxRows, List<string>? warnings = null)
    {
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
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString());
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
            row.Add(cell.ToString());
            if (row.Any(v => v.Length > 0))
            {
                total++;
                if (rows.Count <= maxRows)
                {
                    rows.Add(row);
                }
            }
        }

        var table = MarkdownTable.Build(rows);
        if (total > rows.Count)
        {
            var note = $"_… {total - rows.Count} filas más omitidas (de {total - 1})._";
            warnings?.Add($"Se omitieron {total - rows.Count} filas.");
            return table + "\n\n" + note;
        }

        return table;
    }
}

/// <summary>HTML a Markdown (ReverseMarkdown), quitando scripts, estilos y navegación.</summary>
public sealed partial class HtmlConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => mediaType is MediaTypes.Html or "application/xhtml+xml";

    public Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var html = source.ReadText();
        var title = TitleRegex().Match(html) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        var markdown = ToMarkdown(html);
        return Task.FromResult(DocumentConversionResult.FromPages(source, [new DocumentPage(1, markdown)], title));
    }

    /// <summary>Convierte un fragmento HTML a Markdown.</summary>
    public static string ToMarkdown(string html)
    {
        html = NoiseRegex().Replace(html, string.Empty);
        var body = BodyRegex().Match(html);
        if (body.Success)
        {
            html = body.Groups[1].Value;
        }

        var config = new ReverseMarkdown.Config { GithubFlavored = true };
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        var converter = new ReverseMarkdown.Converter(config);
        var markdown = converter.Convert(html);
        return BlankLinesRegex().Replace(markdown, "\n\n").Trim();
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<(script|style|noscript|template|svg|head|nav|footer|iframe)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex BlankLinesRegex();
}
