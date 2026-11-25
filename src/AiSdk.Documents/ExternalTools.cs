using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AiSdk.Documents;

/// <summary>
/// Execution of command-line tools (tesseract, pdftoppm, ffmpeg) with the global runner
/// <see cref="Runner"/>.
/// </summary>
public static class ExternalProcess
{
    private static IExternalProcessRunner _runner = new ExternalProcessRunner();

    public sealed record Result(int ExitCode, byte[] Output, string Error);

    /// <summary>
    /// Runner used by the SDK and by these static methods when a component does not receive its own. By default it
    /// launches the tool directly, only with the variables in <see cref="ExternalProcessOptions.CommonEnvironmentVariables"/>,
    /// with at most 10 minutes and 64 MB of output; set it at application startup to use a wrapper or change those
    /// limits (see <see cref="ExternalProcessRunner"/>).
    /// </summary>
    public static IExternalProcessRunner Runner
    {
        get => Volatile.Read(ref _runner);
        set => Volatile.Write(ref _runner, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Indicates whether the executable exists (absolute path or on the PATH), according to <see cref="Runner"/>.</summary>
    public static bool Exists(string executable) => Runner.Exists(executable);

    public static Task<Result> RunAsync(string executable, IEnumerable<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default) =>
        Runner.RunAsync(executable, arguments.ToList(), input, cancellationToken);

    /// <summary>Runs the tool and throws an exception with the standard error if the exit code is not 0.</summary>
    public static Task<byte[]> RunCheckedAsync(string executable, IEnumerable<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default) =>
        Runner.RunCheckedAsync(executable, arguments.ToList(), input, cancellationToken);
}

/// <summary>
/// Temporary directory that is deleted when disposed. Created with <see cref="Directory.CreateTempSubdirectory"/>: on
/// Unix, with 0700 permissions, so other users on the machine cannot read the documents or the rasterized pages.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("aisdk-").FullName;
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Local OCR with <see href="https://github.com/tesseract-ocr/tesseract">Tesseract</see> (without sending data to
/// third parties). Requires the <c>tesseract</c> executable and the installed languages
/// (e.g. <c>apt install tesseract-ocr tesseract-ocr-spa</c>).
/// </summary>
public sealed partial class TesseractOcr : IOcrEngine
{
    private static readonly Dictionary<string, string> IsoToTesseract = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = "spa", ["en"] = "eng", ["pt"] = "por", ["fr"] = "fra", ["de"] = "deu", ["it"] = "ita",
        ["ca"] = "cat", ["nl"] = "nld", ["ja"] = "jpn", ["zh"] = "chi_sim", ["ko"] = "kor", ["ru"] = "rus",
    };

    private readonly string _executable;
    private readonly IExternalProcessRunner? _runner;
    private readonly string? _languages;
    private readonly int? _pageSegmentationMode;
    private HashSet<string>? _installed;

    /// <param name="languages">Tesseract languages (e.g. <c>"spa+eng"</c>). If null, they are inferred from the request language.</param>
    /// <param name="executable">Path or name of the executable.</param>
    /// <param name="pageSegmentationMode"><c>--psm</c> mode (Tesseract's default, 3, by default).</param>
    /// <param name="runner">Process runner; null uses <see cref="ExternalProcess.Runner"/>.</param>
    public TesseractOcr(string? languages = null, string executable = "tesseract", int? pageSegmentationMode = null, IExternalProcessRunner? runner = null)
    {
        _languages = languages;
        _executable = executable;
        _pageSegmentationMode = pageSegmentationMode;
        _runner = runner;
    }

    private IExternalProcessRunner Runner => _runner ?? ExternalProcess.Runner;

    public string Name => "tesseract";

    /// <summary>Indicates whether Tesseract is installed.</summary>
    public bool IsAvailable => Runner.Exists(_executable);

    public bool Supports(string mediaType) => mediaType is "image/png" or "image/jpeg" or "image/tiff" or "image/bmp" or "image/gif" or "image/webp";

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "stdin", "stdout", "-l", await ResolveLanguagesAsync(request.Language, cancellationToken).ConfigureAwait(false) };
        if (_pageSegmentationMode is { } psm)
        {
            arguments.AddRange(["--psm", psm.ToString(CultureInfo.InvariantCulture)]);
        }

        var output = await Runner.RunCheckedAsync(_executable, arguments, request.Data, cancellationToken).ConfigureAwait(false);
        return new OcrResult([new OcrPage(1, Clean(Encoding.UTF8.GetString(output)))]);
    }

    private async Task<string> ResolveLanguagesAsync(string? iso, CancellationToken cancellationToken)
    {
        if (_languages is not null)
        {
            return _languages;
        }

        if (_installed is null)
        {
            var result = await Runner.RunAsync(_executable, ["--list-langs"], cancellationToken: cancellationToken).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(result.Output) + "\n" + result.Error;
            _installed = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.Contains(' ', StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var wanted = new List<string>();
        if (iso is not null && IsoToTesseract.TryGetValue(iso, out var code) && _installed.Contains(code))
        {
            wanted.Add(code);
        }

        foreach (var fallback in new[] { "spa", "eng" })
        {
            if (_installed.Contains(fallback) && !wanted.Contains(fallback))
            {
                wanted.Add(fallback);
            }
        }

        return wanted.Count == 0 ? "eng" : string.Join('+', wanted);
    }

    /// <summary>Keeps line breaks (important in receipts, forms and tables), rejoins hyphenated words and removes control characters.</summary>
    private static string Clean(string text)
    {
        var paragraphs = text.Replace("\f", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n\n", paragraphs.Select(p => HyphenBreak().Replace(p, "$1")));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\p{L})-\n(?=\p{Ll})")]
    private static partial System.Text.RegularExpressions.Regex HyphenBreak();
}

/// <summary>Converts the pages of a PDF into images (for OCR of scanned documents).</summary>
public interface IPdfRasterizer
{
    /// <summary>Returns one PNG per requested page (numbered from 1).</summary>
    Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="RenderAsync"/>, reading the PDF from a file. The default implementation loads it into
    /// memory; <see cref="PdftoppmRasterizer"/> reads it directly, without copying it.
    /// </summary>
    async Task<IReadOnlyDictionary<int, byte[]>> RenderFileAsync(string path, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default) =>
        await RenderAsync(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), pages, dpi, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Rasterizer based on <c>pdftoppm</c> (Poppler: <c>apt install poppler-utils</c>, <c>brew install poppler</c>).
/// Consecutive pages are rasterized by a single process (up to <see cref="MaxPagesPerProcess"/>) and the ranges, in
/// parallel. If a range fails, it is retried page by page; pages that cannot be rasterized are missing from the result
/// (if none could be rasterized, the error is thrown).
/// </summary>
/// <param name="executable">Path or name of the executable.</param>
/// <param name="runner">Process runner; null uses <see cref="ExternalProcess.Runner"/>.</param>
public sealed class PdftoppmRasterizer(string executable = "pdftoppm", IExternalProcessRunner? runner = null) : IPdfRasterizer
{
    /// <summary>Returns an instance if <c>pdftoppm</c> is installed; otherwise, null.</summary>
    public static PdftoppmRasterizer? TryCreate(string executable = "pdftoppm", IExternalProcessRunner? runner = null) =>
        (runner ?? ExternalProcess.Runner).Exists(executable) ? new PdftoppmRasterizer(executable, runner) : null;

    /// <summary>Concurrent <c>pdftoppm</c> processes when several pages are requested.</summary>
    public int MaxParallelism { get; init; } = 2;

    /// <summary>Consecutive pages rasterized by a single process.</summary>
    public int MaxPagesPerProcess { get; init; } = 8;

    /// <summary>
    /// Maximum width and height of each image, in pixels (10,000 by default; pdftoppm's <c>-W</c>/<c>-H</c>): anything
    /// beyond that is cropped, so a huge page does not take up gigabytes. To reduce the resolution of large pages,
    /// request a lower <c>dpi</c> (<see cref="MarkdownConverter"/> computes it per page with
    /// <see cref="MarkdownConverterOptions.MaxRasterPixels"/>).
    /// </summary>
    public int MaxPixels { get; init; } = 10_000;

    private IExternalProcessRunner Runner => runner ?? ExternalProcess.Runner;

    public async Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var temp = new TempDirectory();
        var input = temp.File("in.pdf");
        await File.WriteAllBytesAsync(input, pdf, cancellationToken).ConfigureAwait(false);
        return await RenderFileAsync(input, pages, dpi, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<int, byte[]>> RenderFileAsync(string path, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(pages);
        var input = Path.GetFullPath(path); // nunca empieza con '-' (no se confunde con una opción)
        var wanted = pages.Where(p => p > 0).Distinct().Order().ToList();
        var images = new ConcurrentDictionary<int, byte[]>();
        if (wanted.Count == 0)
        {
            return images;
        }

        using var output = new TempDirectory();
        Exception? firstError = null;
        await Parallel.ForEachAsync(
            Ranges(wanted, Math.Max(1, MaxPagesPerProcess)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = cancellationToken },
            async (range, ct) =>
            {
                try
                {
                    await RenderRangeAsync(input, output, range.First, range.Last, dpi, images, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                    for (var page = range.First; range.First != range.Last && page <= range.Last; page++)
                    {
                        try
                        {
                            // Una página dañada no debe impedir las demás del tramo.
                            await RenderRangeAsync(input, output, page, page, dpi, images, ct).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }
            }).ConfigureAwait(false);

        if (images.IsEmpty && firstError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(firstError);
        }

        return images;
    }

    /// <summary>Sorted pages grouped into consecutive ranges (e.g. 1,2,3,7 → [1-3], [7-7]) of at most <paramref name="max"/> pages.</summary>
    internal static List<(int First, int Last)> Ranges(IReadOnlyList<int> sortedPages, int max)
    {
        var ranges = new List<(int, int)>();
        for (var i = 0; i < sortedPages.Count; i++)
        {
            var first = sortedPages[i];
            var last = first;
            while (i + 1 < sortedPages.Count && sortedPages[i + 1] == last + 1 && last - first + 1 < max)
            {
                last = sortedPages[++i];
            }

            ranges.Add((first, last));
        }

        return ranges;
    }

    private async Task RenderRangeAsync(string input, TempDirectory output, int first, int last, int dpi, ConcurrentDictionary<int, byte[]> images, CancellationToken cancellationToken)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"r{first}-{last}");
        await Runner.RunCheckedAsync(
            executable,
            ["-r", dpi.ToString(CultureInfo.InvariantCulture), "-png", "-gray",
             "-W", Math.Max(1, MaxPixels).ToString(CultureInfo.InvariantCulture), "-H", Math.Max(1, MaxPixels).ToString(CultureInfo.InvariantCulture),
             "-f", first.ToString(CultureInfo.InvariantCulture), "-l", last.ToString(CultureInfo.InvariantCulture),
             input, output.File(prefix)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // pdftoppm nombra cada página <prefijo>-<número>.png, con el número relleno con ceros según el total de páginas.
        foreach (var file in Directory.EnumerateFiles(output.Path, prefix + "-*.png"))
        {
            var number = Path.GetFileNameWithoutExtension(file)[(prefix.Length + 1)..];
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= first && page <= last)
            {
                images[page] = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Delete(file);
        }
    }
}
