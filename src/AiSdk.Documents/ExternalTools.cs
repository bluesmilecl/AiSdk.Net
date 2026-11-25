using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AiSdk.Documents;

/// <summary>
/// Ejecución de herramientas de línea de comandos (tesseract, pdftoppm, ffmpeg) con el ejecutor global
/// <see cref="Runner"/>.
/// </summary>
public static class ExternalProcess
{
    private static IExternalProcessRunner _runner = new ExternalProcessRunner();

    public sealed record Result(int ExitCode, byte[] Output, string Error);

    /// <summary>
    /// Ejecutor que usan el SDK y estos métodos estáticos cuando un componente no recibe uno propio. Por defecto
    /// lanza la herramienta directamente, solo con las variables de <see cref="ExternalProcessOptions.CommonEnvironmentVariables"/>,
    /// con 10 minutos y 64 MB de salida como máximo; asígnelo al iniciar la aplicación para usar un envoltorio o
    /// cambiar esos límites (ver <see cref="ExternalProcessRunner"/>).
    /// </summary>
    public static IExternalProcessRunner Runner
    {
        get => Volatile.Read(ref _runner);
        set => Volatile.Write(ref _runner, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Indica si el ejecutable existe (ruta absoluta o en el PATH), según <see cref="Runner"/>.</summary>
    public static bool Exists(string executable) => Runner.Exists(executable);

    public static Task<Result> RunAsync(string executable, IEnumerable<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default) =>
        Runner.RunAsync(executable, arguments.ToList(), input, cancellationToken);

    /// <summary>Ejecuta y lanza una excepción con el error estándar si el código de salida no es 0.</summary>
    public static Task<byte[]> RunCheckedAsync(string executable, IEnumerable<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default) =>
        Runner.RunCheckedAsync(executable, arguments.ToList(), input, cancellationToken);
}

/// <summary>
/// Directorio temporal que se elimina al desecharse. Se crea con <see cref="Directory.CreateTempSubdirectory"/>: en
/// Unix, con permisos 0700, así otros usuarios del equipo no leen los documentos ni las páginas rasterizadas.
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
/// OCR local con <see href="https://github.com/tesseract-ocr/tesseract">Tesseract</see> (sin enviar datos
/// a terceros). Requiere el ejecutable <c>tesseract</c> y los idiomas instalados
/// (p. ej. <c>apt install tesseract-ocr tesseract-ocr-spa</c>).
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

    /// <param name="languages">Idiomas de Tesseract (p. ej. <c>"spa+eng"</c>). Si es null se deducen del idioma de la petición.</param>
    /// <param name="executable">Ruta o nombre del ejecutable.</param>
    /// <param name="pageSegmentationMode">Modo <c>--psm</c> (por defecto el de Tesseract, 3).</param>
    /// <param name="runner">Ejecutor de procesos; null usa <see cref="ExternalProcess.Runner"/>.</param>
    public TesseractOcr(string? languages = null, string executable = "tesseract", int? pageSegmentationMode = null, IExternalProcessRunner? runner = null)
    {
        _languages = languages;
        _executable = executable;
        _pageSegmentationMode = pageSegmentationMode;
        _runner = runner;
    }

    private IExternalProcessRunner Runner => _runner ?? ExternalProcess.Runner;

    public string Name => "tesseract";

    /// <summary>Indica si Tesseract está instalado.</summary>
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

    /// <summary>Conserva las líneas (importantes en boletas, formularios y tablas), une palabras cortadas con guion y elimina caracteres de control.</summary>
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

/// <summary>Convierte páginas de un PDF en imágenes (para OCR de documentos escaneados).</summary>
public interface IPdfRasterizer
{
    /// <summary>Devuelve un PNG por página solicitada (números desde 1).</summary>
    Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="RenderAsync"/>, leyendo el PDF desde un archivo. La implementación por defecto lo carga en
    /// memoria; <see cref="PdftoppmRasterizer"/> lo lee directamente, sin copiarlo.
    /// </summary>
    async Task<IReadOnlyDictionary<int, byte[]>> RenderFileAsync(string path, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default) =>
        await RenderAsync(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), pages, dpi, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Rasterizador basado en <c>pdftoppm</c> (Poppler: <c>apt install poppler-utils</c>, <c>brew install poppler</c>).
/// Las páginas consecutivas se rasterizan con un mismo proceso (hasta <see cref="MaxPagesPerProcess"/>) y los tramos,
/// en paralelo. Si un tramo falla se reintenta página por página; las páginas que no se pueden rasterizar no aparecen
/// en el resultado (si no se pudo ninguna, se lanza el error).
/// </summary>
/// <param name="executable">Ruta o nombre del ejecutable.</param>
/// <param name="runner">Ejecutor de procesos; null usa <see cref="ExternalProcess.Runner"/>.</param>
public sealed class PdftoppmRasterizer(string executable = "pdftoppm", IExternalProcessRunner? runner = null) : IPdfRasterizer
{
    /// <summary>Devuelve una instancia si <c>pdftoppm</c> está instalado; si no, null.</summary>
    public static PdftoppmRasterizer? TryCreate(string executable = "pdftoppm", IExternalProcessRunner? runner = null) =>
        (runner ?? ExternalProcess.Runner).Exists(executable) ? new PdftoppmRasterizer(executable, runner) : null;

    /// <summary>Procesos de <c>pdftoppm</c> simultáneos cuando se piden varias páginas.</summary>
    public int MaxParallelism { get; init; } = 2;

    /// <summary>Páginas consecutivas que rasteriza un mismo proceso.</summary>
    public int MaxPagesPerProcess { get; init; } = 8;

    /// <summary>
    /// Ancho y alto máximos de cada imagen, en píxeles (por defecto 10.000; <c>-W</c>/<c>-H</c> de pdftoppm): lo que
    /// exceda se recorta, así una página enorme no ocupa gigabytes. Para reducir la resolución de las páginas grandes,
    /// pida un <c>dpi</c> menor (<see cref="MarkdownConverter"/> lo calcula por página con
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

    /// <summary>Páginas ordenadas agrupadas en tramos consecutivos (p. ej. 1,2,3,7 → [1-3], [7-7]) de a lo más <paramref name="max"/> páginas.</summary>
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
