using System.Globalization;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using AddedFont = UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont;

namespace AiSdk.Documents.Generation;

/// <summary>Fuentes TrueType usadas en los PDF generados.</summary>
public sealed class PdfFonts
{
    public required byte[] Regular { get; init; }

    public required byte[] Bold { get; init; }

    public required byte[] Italic { get; init; }

    public required byte[] BoldItalic { get; init; }

    public required byte[] Mono { get; init; }

    /// <summary>
    /// Fuentes incluidas en el paquete ("AiSdk Sans" y "AiSdk Mono", derivadas de Liberation, licencia OFL):
    /// latín (incluidos acentos y ñ), griego, puntuación, monedas, flechas y símbolos matemáticos.
    /// Para otros alfabetos (cirílico, CJK, árabe...) pase sus propias fuentes.
    /// </summary>
    public static PdfFonts Default => DefaultFonts.Value;

    /// <summary>Usa la misma fuente para todas las variantes (útil con fuentes CJK como Noto Sans CJK).</summary>
    public static PdfFonts FromSingle(byte[] font, byte[]? mono = null) =>
        new() { Regular = font, Bold = font, Italic = font, BoldItalic = font, Mono = mono ?? font };

    private static readonly Lazy<PdfFonts> DefaultFonts = new(() => new PdfFonts
    {
        Regular = Resource("AiSdkSans-Regular.ttf"),
        Bold = Resource("AiSdkSans-Bold.ttf"),
        Italic = Resource("AiSdkSans-Italic.ttf"),
        BoldItalic = Resource("AiSdkSans-BoldItalic.ttf"),
        Mono = Resource("AiSdkMono-Regular.ttf"),
    });

    private static byte[] Resource(string name)
    {
        using var stream = typeof(PdfFonts).Assembly.GetManifestResourceStream("AiSdk.Documents.Fonts." + name)
                           ?? throw new InvalidOperationException($"Falta la fuente incrustada {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>Markdown → PDF con diagramación propia (sin navegador ni dependencias nativas).</summary>
public static class PdfWriter
{
    public static byte[] FromMarkdown(string markdown, DocumentStyleOptions? options = null) =>
        Write(MarkdownModel.Parse(markdown), options);

    public static byte[] Write(IReadOnlyList<DocBlock> blocks, DocumentStyleOptions? options = null)
    {
        using var layout = new PdfLayout(options ?? new DocumentStyleOptions());
        return layout.Render(blocks);
    }
}

internal sealed class PdfLayout : IDisposable
{
    private const double LineFactor = 1.38;

    private readonly DocumentStyleOptions _options;
    private readonly PdfDocumentBuilder _builder = new();
    private readonly AddedFont[] _fonts;
    private readonly Dictionary<(int Font, char C), double> _widths = [];
    private readonly List<PdfPageBuilder> _pages = [];
    private readonly double _pageWidth;
    private readonly double _pageHeight;
    private readonly double _margin;
    private readonly (byte R, byte G, byte B) _accent;
    private PdfPageBuilder _page = null!;
    private double _y;

    // Índices de fuente.
    private const int Regular = 0, Bold = 1, Italic = 2, BoldItalic = 3, Mono = 4;

    public PdfLayout(DocumentStyleOptions options)
    {
        _options = options;
        var fonts = options.PdfFonts ?? PdfFonts.Default;
        var cache = new Dictionary<byte[], AddedFont>(ReferenceEqualityComparer.Instance);
        AddedFont Add(byte[] data) => cache.TryGetValue(data, out var f) ? f : cache[data] = _builder.AddTrueTypeFont(data);
        _fonts = [Add(fonts.Regular), Add(fonts.Bold), Add(fonts.Italic), Add(fonts.BoldItalic), Add(fonts.Mono)];

        (_pageWidth, _pageHeight) = options.PaperSize switch
        {
            PaperSize.Letter => (612d, 792d),
            PaperSize.Legal => (612d, 1008d),
            _ => (595.28, 841.89),
        };
        if (options.Landscape) (_pageWidth, _pageHeight) = (_pageHeight, _pageWidth);
        _margin = options.MarginCm * 28.3465;
        _accent = Hex(options.AccentColor);
        _builder.DocumentInformation.Title = options.Title;
        _builder.DocumentInformation.Author = options.Author;
        _builder.DocumentInformation.Producer = "AiSdk.Documents";
    }

    public void Dispose() => _builder.Dispose();

    private double Left => _margin;

    private double Right => _pageWidth - _margin;

    private double Top => _pageHeight - _margin - (_options.HeaderText is null ? 0 : 14);

    private double Bottom => _margin + (_options.PageNumbers ? 16 : 0);

    private double BaseSize => _options.FontSize;

    public byte[] Render(IReadOnlyList<DocBlock> blocks)
    {
        NewPage();
        if (_options.TitlePage && _options.Title is not null)
        {
            DrawRuns([new TextRun(_options.Title, Bold: true)], Left, Right, BaseSize * 2.3, _accent);
            if (_options.Subtitle is not null) DrawRuns([new TextRun(_options.Subtitle)], Left, Right, BaseSize * 1.3, (89, 89, 89));
            var byline = string.Join(" · ", new[] { _options.Author, DateTime.Now.ToString("d 'de' MMMM 'de' yyyy", new CultureInfo("es-ES")) }.Where(s => s is not null));
            DrawRuns([new TextRun(byline)], Left, Right, BaseSize, (110, 110, 110));
            _y -= BaseSize * 1.5;
        }

        foreach (var block in blocks)
        {
            RenderBlock(block, Left, Right, quote: false);
        }

        DecoratePages();
        return _builder.Build();
    }

    private void RenderBlock(DocBlock block, double left, double right, bool quote)
    {
        switch (block)
        {
            case HeadingBlockModel heading:
                double[] scale = [1.9, 1.5, 1.25, 1.1, 1.0, 1.0];
                var size = BaseSize * scale[Math.Clamp(heading.Level, 1, 6) - 1];
                var before = heading.Level <= 2 ? size * 0.9 : size * 0.6;
                // Mantener el título con al menos dos líneas del texto siguiente.
                EnsureSpace(before + size * LineFactor + BaseSize * LineFactor * 2);
                if (!AtTop()) _y -= before;
                DrawRuns(heading.Runs.Select(r => r with { Bold = true }).ToList(), left, right, size, _accent);
                if (heading.Level == 1)
                {
                    _page.SetStrokeColor(_accent.R, _accent.G, _accent.B);
                    _page.DrawLine(new PdfPoint(left, _y + 3), new PdfPoint(right, _y + 3), 0.8);
                    _page.ResetColor();
                    _y -= 4;
                }

                _y -= size * 0.25;
                break;

            case ParagraphModel paragraph:
                DrawRuns(quote ? paragraph.Runs.Select(r => r with { Italic = true }).ToList() : paragraph.Runs, left, right, BaseSize, quote ? ((byte)90, (byte)90, (byte)90) : ((byte)25, (byte)25, (byte)25));
                _y -= BaseSize * 0.55;
                break;

            case ListModel list:
                var number = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Ordered ? $"{number++}." : "•";
                    if (item.Checked is { } done) marker = done ? "[x]" : "[ ]";
                    var indent = list.Ordered || item.Checked is not null ? 20 : 14;
                    var first = true;
                    foreach (var child in item.Blocks)
                    {
                        if (first && child is ParagraphModel p)
                        {
                            EnsureSpace(BaseSize * LineFactor);
                            var markerY = _y - BaseSize;
                            DrawText(marker, left + 2, markerY, list.Ordered ? Regular : Bold, BaseSize, (60, 60, 60));
                            DrawRuns(p.Runs, left + indent, right, BaseSize, (25, 25, 25));
                            _y -= BaseSize * 0.2;
                        }
                        else
                        {
                            RenderBlock(child, left + indent, right, quote);
                        }

                        first = false;
                    }
                }

                _y -= BaseSize * 0.35;
                break;

            case CodeBlockModel code:
                var codeSize = BaseSize * 0.82;
                var lineHeight = codeSize * 1.35;
                var lines = code.Code.Split('\n').SelectMany(l => WrapChars(l, Mono, codeSize, right - left - 12)).ToList();
                _y -= 2;
                foreach (var line in lines)
                {
                    EnsureSpace(lineHeight + 4);
                    Fill(left, _y - lineHeight - 2, right - left, lineHeight + 2, (245, 245, 245));
                    DrawText(line, left + 6, _y - codeSize - 1, Mono, codeSize, (40, 40, 40));
                    _y -= lineHeight;
                }

                _y -= BaseSize * 0.8;
                break;

            case QuoteModel q:
                var start = _y;
                var startPage = _page;
                foreach (var child in q.Blocks)
                {
                    RenderBlock(child, left + 12, right, quote: true);
                }

                if (ReferenceEquals(startPage, _page))
                {
                    _page.SetStrokeColor(190, 190, 190);
                    _page.DrawLine(new PdfPoint(left + 3, start - 2), new PdfPoint(left + 3, _y + BaseSize * 0.5), 2.5);
                    _page.ResetColor();
                }

                break;

            case TableModel table:
                RenderTable(table, left, right);
                break;

            case RuleModel:
                EnsureSpace(12);
                _y -= 6;
                _page.SetStrokeColor(200, 200, 200);
                _page.DrawLine(new PdfPoint(left, _y), new PdfPoint(right, _y), 0.6);
                _page.ResetColor();
                _y -= 8;
                break;

            case PageBreakModel:
                if (!AtTop()) NewPage();
                break;
        }
    }

    private void RenderTable(TableModel table, double left, double right)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        var columns = table.Rows.Max(r => r.Count);
        const double pad = 4;
        var size = BaseSize * 0.9;
        var lineHeight = size * 1.3;
        var available = right - left;

        // Ancho natural y mínimo por columna; se reparte el espacio disponible.
        var natural = new double[columns];
        var minimum = new double[columns];
        foreach (var row in table.Rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var text = string.Concat(row[c].Select(r => r.Text));
                natural[c] = Math.Max(natural[c], Width(text, Bold, size) + 2 * pad);
                var longest = text.Split(' ').DefaultIfEmpty(string.Empty).Max(w => Width(w, Bold, size));
                minimum[c] = Math.Max(minimum[c], Math.Min(longest + 2 * pad, available / columns));
            }
        }

        var widths = natural.Sum() <= available
            ? natural.Select(w => w * available / Math.Max(1, natural.Sum())).ToArray()
            : Distribute(natural, minimum, available);

        void DrawRow(IReadOnlyList<IReadOnlyList<TextRun>> row, int index)
        {
            var header = index == 0;
            var cells = Enumerable.Range(0, columns).Select(c =>
            {
                var runs = c < row.Count ? row[c] : [];
                return header ? runs.Select(r => r with { Bold = true }).ToList() : runs.ToList();
            }).ToList();
            var wrapped = cells.Select((runs, c) => Wrap(runs, widths[c] - 2 * pad, size)).ToList();
            var height = wrapped.Max(w => Math.Max(1, w.Count)) * lineHeight + 2 * pad;
            if (_y - height < Bottom)
            {
                NewPage();
                if (!header) DrawRow(table.Rows[0], 0);
            }

            var x = left;
            if (header) Fill(left, _y - height, available, height, _accent);
            else if (index % 2 == 0) Fill(left, _y - height, available, height, (246, 248, 250));

            for (var c = 0; c < columns; c++)
            {
                var align = c < table.Align.Count ? table.Align[c] : CellAlign.Left;
                var lineY = _y - pad - size;
                foreach (var line in wrapped[c])
                {
                    var lineWidth = line.Sum(t => t.Width);
                    var offset = align switch
                    {
                        CellAlign.Right => widths[c] - 2 * pad - lineWidth,
                        CellAlign.Center => (widths[c] - 2 * pad - lineWidth) / 2,
                        _ => 0,
                    };
                    var tx = x + pad + offset;
                    foreach (var token in line)
                    {
                        DrawText(token.Text, tx, lineY, token.Font, size, header ? ((byte)255, (byte)255, (byte)255) : token.Color ?? ((byte)25, (byte)25, (byte)25), token.Link);
                        tx += token.Width;
                    }

                    lineY -= lineHeight;
                }

                x += widths[c];
            }

            _page.SetStrokeColor(200, 200, 200);
            _page.DrawLine(new PdfPoint(left, _y - height), new PdfPoint(right, _y - height), 0.5);
            x = left;
            for (var c = 0; c <= columns; c++)
            {
                _page.DrawLine(new PdfPoint(x, _y), new PdfPoint(x, _y - height), 0.5);
                if (c < columns) x += widths[c];
            }

            if (header) _page.DrawLine(new PdfPoint(left, _y), new PdfPoint(right, _y), 0.5);
            _page.ResetColor();
            _y -= height;
        }

        EnsureSpace(lineHeight * 3 + 4 * pad);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            DrawRow(table.Rows[r], r);
        }

        _y -= BaseSize * 0.9;
    }

    private static double[] Distribute(double[] natural, double[] minimum, double available)
    {
        var widths = minimum.ToArray();
        var remaining = available - widths.Sum();
        if (remaining <= 0)
        {
            return widths.Select(w => w * available / widths.Sum()).ToArray();
        }

        var extra = natural.Select((n, i) => Math.Max(0, n - minimum[i])).ToArray();
        var totalExtra = extra.Sum();
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] += totalExtra == 0 ? remaining / widths.Length : remaining * extra[i] / totalExtra;
        }

        return widths;
    }

    // ---------------------------------------------------------------------
    // Texto
    // ---------------------------------------------------------------------

    private sealed record Token(string Text, int Font, double Width, string? Link, bool Strike, bool Code, (byte R, byte G, byte B)? Color);

    private static int FontOf(TextRun run) => run.Code ? Mono : run.Bold && run.Italic ? BoldItalic : run.Bold ? Bold : run.Italic ? Italic : Regular;

    /// <summary>Parte los fragmentos en líneas que caben en <paramref name="width"/>.</summary>
    private List<List<Token>> Wrap(IReadOnlyList<TextRun> runs, double width, double size)
    {
        var lines = new List<List<Token>> { new() };
        double used = 0;
        foreach (var run in runs)
        {
            var font = FontOf(run);
            (byte, byte, byte)? color = run.Link is not null ? ((byte)5, (byte)99, (byte)193) : run.Code ? ((byte)150, (byte)40, (byte)60) : null;
            var segments = run.Text.Split('\n');
            for (var s = 0; s < segments.Length; s++)
            {
                if (s > 0)
                {
                    lines.Add([]);
                    used = 0;
                }

                // Palabras con su espacio final, para respetar el espaciado original.
                foreach (var word in SplitKeepingSpaces(Sanitize(segments[s], font)))
                {
                    var w = Width(word, font, size);
                    if (used + Width(word.TrimEnd(), font, size) > width && lines[^1].Count > 0)
                    {
                        lines.Add([]);
                        used = 0;
                        if (word.Trim().Length == 0) continue;
                    }

                    if (w > width)
                    {
                        foreach (var piece in WrapChars(word, font, size, width))
                        {
                            if (lines[^1].Count > 0) { lines.Add([]); used = 0; }
                            var pw = Width(piece, font, size);
                            lines[^1].Add(new Token(piece, font, pw, run.Link, run.Strike, run.Code, color));
                            used = pw;
                        }

                        continue;
                    }

                    lines[^1].Add(new Token(word, font, w, run.Link, run.Strike, run.Code, color));
                    used += w;
                }
            }
        }

        // Quita espacios al inicio de cada línea.
        foreach (var line in lines)
        {
            while (line.Count > 0 && line[0].Text.Trim().Length == 0) line.RemoveAt(0);
        }

        return lines.Where((l, i) => l.Count > 0 || i == 0).ToList();
    }

    private static IEnumerable<string> SplitKeepingSpaces(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length) yield return text[start..];
    }

    private IEnumerable<string> WrapChars(string text, int font, double size, double width)
    {
        text = Sanitize(text, font);
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var start = 0;
        double used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var w = CharWidth(font, text[i]) * size;
            if (used + w > width && i > start)
            {
                yield return text[start..i];
                start = i;
                used = 0;
            }

            used += w;
        }

        yield return text[start..];
    }

    /// <summary>Dibuja fragmentos con ajuste de línea desde la posición actual.</summary>
    private void DrawRuns(IReadOnlyList<TextRun> runs, double left, double right, double size, (byte R, byte G, byte B) color)
    {
        var lineHeight = size * LineFactor;
        foreach (var line in Wrap(runs, right - left, size))
        {
            EnsureSpace(lineHeight);
            var x = left;
            var baseline = _y - size;
            foreach (var token in line)
            {
                if (token.Code)
                {
                    Fill(x, baseline - size * 0.22, token.Width, size * 1.12, (243, 243, 243));
                }

                DrawText(token.Text, x, baseline, token.Font, size, token.Color ?? color, token.Link);
                if (token.Strike)
                {
                    _page.SetStrokeColor(color.R, color.G, color.B);
                    _page.DrawLine(new PdfPoint(x, baseline + size * 0.3), new PdfPoint(x + token.Width, baseline + size * 0.3), 0.6);
                    _page.ResetColor();
                }

                x += token.Width;
            }

            _y -= lineHeight;
        }
    }

    private void DrawText(string text, double x, double baseline, int font, double size, (byte R, byte G, byte B) color, string? link = null)
    {
        text = Sanitize(text, font);
        if (text.Trim().Length == 0)
        {
            return;
        }

        _page.SetTextAndFillColor(color.R, color.G, color.B);
        _page.AddText(text, size, new PdfPoint(x, baseline), _fonts[font]);
        _page.ResetColor();
        if (link is not null && Uri.TryCreate(link, UriKind.Absolute, out _))
        {
            var width = Width(text.TrimEnd(), font, size);
            // Sin borde visible: el enlace se indica con color y subrayado.
            _page.AddLink(new LinkAnnotation(
                new UglyToad.PdfPig.Actions.UriAction(link),
                new PdfRectangle(x, baseline - size * 0.2, x + width, baseline + size * 0.9),
                new UglyToad.PdfPig.Annotations.AnnotationBorder(0, 0, 0, null),
                null,
                null,
                null));
            _page.SetStrokeColor(color.R, color.G, color.B);
            _page.DrawLine(new PdfPoint(x, baseline - 1.2), new PdfPoint(x + width, baseline - 1.2), 0.5);
            _page.ResetColor();
        }
    }

    private void Fill(double x, double y, double width, double height, (byte R, byte G, byte B) color)
    {
        _page.SetTextAndFillColor(color.R, color.G, color.B);
        _page.SetStrokeColor(color.R, color.G, color.B);
        _page.DrawRectangle(new PdfPoint(x, y), width, height, 0.1, fill: true);
        _page.ResetColor();
    }

    private double Width(string text, int font, double size)
    {
        double total = 0;
        foreach (var c in text)
        {
            total += CharWidth(font, c);
        }

        return total * size;
    }

    /// <summary>Ancho del carácter a tamaño 1 (NaN si la fuente no lo tiene).</summary>
    private double CharWidth(int font, char c)
    {
        if (_widths.TryGetValue((font, c), out var width))
        {
            return double.IsNaN(width) ? CharWidth(font, '?') : width;
        }

        try
        {
            var letters = _page.MeasureText(c.ToString(), 100, new PdfPoint(0, 0), _fonts[font]);
            width = letters.Count == 0 ? 0 : (letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X) / 100;
        }
        catch (InvalidOperationException)
        {
            width = double.NaN;
        }

        _widths[(font, c)] = width;
        return double.IsNaN(width) ? CharWidth(font, '?') : width;
    }

    /// <summary>Reemplaza caracteres que la fuente no tiene (emojis, otros alfabetos) por equivalentes o '?'.</summary>
    private string Sanitize(string text, int font)
    {
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        var changed = false;
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
            {
                // Emojis y caracteres fuera del plano básico: un único sustituto.
                buffer[length++] = '?';
                i++;
                changed = true;
                continue;
            }

            var c = text[i] switch
            {
                '\t' => ' ',
                ' ' => ' ',
                _ => text[i],
            };
            if (c != ' ' && double.IsNaN(_widths.TryGetValue((font, c), out var w) ? w : ProbeWidth(font, c)))
            {
                c = c switch
                {
                    '✓' or '✔' or '☑' => 'x',
                    '☐' => ' ',
                    _ => '?',
                };
            }

            changed |= c != text[i];
            buffer[length++] = c;
        }

        return changed ? new string(buffer[..length]) : text;
    }

    private double ProbeWidth(int font, char c)
    {
        CharWidth(font, c);
        return _widths[(font, c)];
    }

    // ---------------------------------------------------------------------
    // Páginas
    // ---------------------------------------------------------------------

    private bool AtTop() => _y >= Top - 0.5;

    private void EnsureSpace(double height)
    {
        if (_y - height < Bottom && !AtTop())
        {
            NewPage();
        }
    }

    private void NewPage()
    {
        _page = _builder.AddPage(_pageWidth, _pageHeight);
        _pages.Add(_page);
        _y = Top;
    }

    private void DecoratePages()
    {
        var size = BaseSize * 0.8;
        for (var i = 0; i < _pages.Count; i++)
        {
            _page = _pages[i];
            if (_options.HeaderText is { } header)
            {
                var width = Width(Sanitize(header, Regular), Regular, size);
                DrawText(header, Right - width, _pageHeight - _margin + 2, Regular, size, (127, 127, 127));
            }

            if (_options.PageNumbers)
            {
                var text = $"Página {i + 1} de {_pages.Count}";
                var width = Width(text, Regular, size);
                DrawText(text, (_pageWidth - width) / 2, _margin - 2, Regular, size, (127, 127, 127));
            }
        }
    }

    private static (byte R, byte G, byte B) Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length == 6
            ? (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16))
            : ((byte)31, (byte)78, (byte)121);
    }
}
