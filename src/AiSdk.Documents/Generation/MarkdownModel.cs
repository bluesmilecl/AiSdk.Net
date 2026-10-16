using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;

namespace AiSdk.Documents.Generation;

/// <summary>Fragmento de texto con formato.</summary>
public sealed record TextRun(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null, bool Strike = false);

/// <summary>Bloque de un documento (modelo intermedio común a Word y PDF).</summary>
public abstract record DocBlock;

public sealed record HeadingBlockModel(int Level, IReadOnlyList<TextRun> Runs) : DocBlock;

public sealed record ParagraphModel(IReadOnlyList<TextRun> Runs) : DocBlock;

public sealed record ListItemModel(IReadOnlyList<DocBlock> Blocks, bool? Checked = null);

public sealed record ListModel(bool Ordered, int Start, IReadOnlyList<ListItemModel> Items) : DocBlock;

public sealed record CodeBlockModel(string Code, string? Language) : DocBlock;

public sealed record QuoteModel(IReadOnlyList<DocBlock> Blocks) : DocBlock;

public enum CellAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Tabla; la primera fila es el encabezado.</summary>
public sealed record TableModel(IReadOnlyList<IReadOnlyList<IReadOnlyList<TextRun>>> Rows, IReadOnlyList<CellAlign> Align) : DocBlock;

public sealed record RuleModel : DocBlock;

public sealed record PageBreakModel : DocBlock;

/// <summary>Convierte Markdown (GFM: tablas, listas de tareas, tachado) al modelo intermedio.</summary>
public static class MarkdownModel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<DocBlock> Parse(string markdown)
    {
        var document = Markdown.Parse(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), Pipeline);
        return Blocks(document);
    }

    private static List<DocBlock> Blocks(ContainerBlock container)
    {
        var result = new List<DocBlock>();
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    result.Add(new HeadingBlockModel(heading.Level, Runs(heading.Inline)));
                    break;
                case ParagraphBlock paragraph:
                    var runs = Runs(paragraph.Inline);
                    if (runs.Count == 1 && runs[0].Text.Trim() is @"\pagebreak" or @"\newpage")
                    {
                        result.Add(new PageBreakModel());
                    }
                    else if (runs.Count > 0)
                    {
                        result.Add(new ParagraphModel(runs));
                    }

                    break;
                case ListBlock list:
                    var items = list.OfType<ListItemBlock>().Select(item =>
                    {
                        var task = item.Descendants<TaskList>().FirstOrDefault();
                        return new ListItemModel(Blocks(item), task?.Checked);
                    }).ToList();
                    var start = list.IsOrdered && int.TryParse(list.OrderedStart, out var s) ? s : 1;
                    result.Add(new ListModel(list.IsOrdered, start, items));
                    break;
                case FencedCodeBlock fenced:
                    result.Add(new CodeBlockModel(Lines(fenced), string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info));
                    break;
                case CodeBlock code:
                    result.Add(new CodeBlockModel(Lines(code), null));
                    break;
                case QuoteBlock quote:
                    result.Add(new QuoteModel(Blocks(quote)));
                    break;
                case MdTable table:
                    var rows = table.OfType<TableRow>()
                        .Select(r => (IReadOnlyList<IReadOnlyList<TextRun>>)r.OfType<TableCell>()
                            .Select(c => (IReadOnlyList<TextRun>)c.OfType<ParagraphBlock>().SelectMany(p => Runs(p.Inline)).ToList())
                            .ToList())
                        .ToList();
                    var align = table.ColumnDefinitions.Select(d => d.Alignment switch
                    {
                        TableColumnAlign.Center => CellAlign.Center,
                        TableColumnAlign.Right => CellAlign.Right,
                        _ => CellAlign.Left,
                    }).ToList();
                    result.Add(new TableModel(rows, align));
                    break;
                case ThematicBreakBlock:
                    result.Add(new RuleModel());
                    break;
                case HtmlBlock html when Lines(html).Contains("pagebreak", StringComparison.OrdinalIgnoreCase):
                    result.Add(new PageBreakModel());
                    break;
                case HtmlBlock html:
                    var text = System.Text.RegularExpressions.Regex.Replace(Lines(html), "<[^>]+>", string.Empty).Trim();
                    if (text.Length > 0)
                    {
                        result.Add(new ParagraphModel([new TextRun(System.Net.WebUtility.HtmlDecode(text))]));
                    }

                    break;
                case ContainerBlock nested:
                    result.AddRange(Blocks(nested));
                    break;
            }
        }

        return result;
    }

    private static string Lines(LeafBlock block)
    {
        var builder = new StringBuilder();
        var lines = block.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    private static List<TextRun> Runs(ContainerInline? inline)
    {
        var runs = new List<TextRun>();
        if (inline is not null)
        {
            Collect(inline, runs, bold: false, italic: false, strike: false, link: null);
        }

        // Une fragmentos contiguos con el mismo formato.
        var merged = new List<TextRun>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && merged[^1] with { Text = run.Text } == run)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            }
            else
            {
                merged.Add(run);
            }
        }

        if (merged.Count > 0)
        {
            merged[0] = merged[0] with { Text = merged[0].Text.TrimStart() };
            merged[^1] = merged[^1] with { Text = merged[^1].Text.TrimEnd() };
        }

        return merged.Where(r => r.Text.Length > 0).ToList();
    }

    private static void Collect(Inline? inline, List<TextRun> runs, bool bold, bool italic, bool strike, string? link)
    {
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    runs.Add(new TextRun(literal.Content.ToString(), bold, italic, false, link, strike));
                    break;
                case CodeInline code:
                    runs.Add(new TextRun(code.Content, bold, italic, true, link, strike));
                    break;
                case LineBreakInline lineBreak:
                    runs.Add(new TextRun(lineBreak.IsHard ? "\n" : " ", bold, italic, false, link, strike));
                    break;
                case EmphasisInline emphasis:
                    var isStrike = emphasis.DelimiterChar == '~';
                    Collect(
                        emphasis.FirstChild,
                        runs,
                        bold || (!isStrike && emphasis.DelimiterCount >= 2),
                        italic || (!isStrike && emphasis.DelimiterCount % 2 == 1),
                        strike || isStrike,
                        link);
                    break;
                case LinkInline { IsImage: true } image:
                    var alt = new List<TextRun>();
                    Collect(image.FirstChild, alt, false, false, false, null);
                    runs.Add(new TextRun($"[{string.Concat(alt.Select(a => a.Text))}]", bold, true, false, image.Url, strike));
                    break;
                case LinkInline linkInline:
                    Collect(linkInline.FirstChild, runs, bold, italic, strike, linkInline.Url);
                    break;
                case AutolinkInline auto:
                    runs.Add(new TextRun(auto.Url, bold, italic, false, auto.IsEmail ? "mailto:" + auto.Url : auto.Url, strike));
                    break;
                case HtmlEntityInline entity:
                    runs.Add(new TextRun(entity.Transcoded.ToString(), bold, italic, false, link, strike));
                    break;
                case TaskList:
                    break;
                case ContainerInline container:
                    Collect(container.FirstChild, runs, bold, italic, strike, link);
                    break;
            }
        }
    }
}
