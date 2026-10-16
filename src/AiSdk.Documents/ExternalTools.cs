using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AiSdk.Documents;

/// <summary>Ejecución de herramientas de línea de comandos (tesseract, pdftoppm, ffmpeg).</summary>
public static class ExternalProcess
{
    private static readonly ConcurrentDictionary<string, bool> Available = new();

    public sealed record Result(int ExitCode, byte[] Output, string Error);

    /// <summary>Indica si el ejecutable existe (ruta absoluta o en el PATH).</summary>
    public static bool Exists(string executable) => Available.GetOrAdd(executable, static name =>
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name);
        }

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : [string.Empty];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir, name + ext))));
    });

    public static async Task<Result> RunAsync(string executable, IEnumerable<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar '{executable}'.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        using var output = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new Result(process.ExitCode, output.ToArray(), await stderr.ConfigureAwait(false));
    }

    /// <summary>Ejecuta y lanza una excepción con el error estándar si el código de salida no es 0.</summary>
    public static async Task<byte[]> RunCheckedAsync(string executable, IEnumerable<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(executable, arguments, input, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"'{Path.GetFileName(executable)}' terminó con código {result.ExitCode}: {result.Error.Trim()}");
    }
}

/// <summary>Directorio temporal que se elimina al desecharse.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aisdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
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
    private readonly string? _languages;
    private readonly int? _pageSegmentationMode;
    private IReadOnlySet<string>? _installed;

    /// <param name="languages">Idiomas de Tesseract (p. ej. <c>"spa+eng"</c>). Si es null se deducen del idioma de la petición.</param>
    /// <param name="executable">Ruta o nombre del ejecutable.</param>
    /// <param name="pageSegmentationMode">Modo <c>--psm</c> (por defecto el de Tesseract, 3).</param>
    public TesseractOcr(string? languages = null, string executable = "tesseract", int? pageSegmentationMode = null)
    {
        _languages = languages;
        _executable = executable;
        _pageSegmentationMode = pageSegmentationMode;
    }

    public string Name => "tesseract";

    /// <summary>Indica si Tesseract está instalado.</summary>
    public bool IsAvailable => ExternalProcess.Exists(_executable);

    public bool Supports(string mediaType) => mediaType is "image/png" or "image/jpeg" or "image/tiff" or "image/bmp" or "image/gif" or "image/webp";

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "stdin", "stdout", "-l", await ResolveLanguagesAsync(request.Language, cancellationToken).ConfigureAwait(false) };
        if (_pageSegmentationMode is { } psm)
        {
            arguments.AddRange(["--psm", psm.ToString(CultureInfo.InvariantCulture)]);
        }

        var output = await ExternalProcess.RunCheckedAsync(_executable, arguments, request.Data, cancellationToken).ConfigureAwait(false);
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
            var result = await ExternalProcess.RunAsync(_executable, ["--list-langs"], cancellationToken: cancellationToken).ConfigureAwait(false);
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
}

/// <summary>Rasterizador basado en <c>pdftoppm</c> (Poppler: <c>apt install poppler-utils</c>, <c>brew install poppler</c>).</summary>
public sealed class PdftoppmRasterizer(string executable = "pdftoppm") : IPdfRasterizer
{
    /// <summary>Devuelve una instancia si <c>pdftoppm</c> está instalado; si no, null.</summary>
    public static PdftoppmRasterizer? TryCreate(string executable = "pdftoppm") =>
        ExternalProcess.Exists(executable) ? new PdftoppmRasterizer(executable) : null;

    public async Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default)
    {
        using var temp = new TempDirectory();
        var input = temp.File("in.pdf");
        await File.WriteAllBytesAsync(input, pdf, cancellationToken).ConfigureAwait(false);
        var images = new Dictionary<int, byte[]>();
        foreach (var page in pages)
        {
            var number = page.ToString(CultureInfo.InvariantCulture);
            var prefix = temp.File($"p{number}");
            await ExternalProcess.RunCheckedAsync(
                executable,
                ["-r", dpi.ToString(CultureInfo.InvariantCulture), "-png", "-gray", "-f", number, "-l", number, "-singlefile", input, prefix],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            images[page] = await File.ReadAllBytesAsync(prefix + ".png", cancellationToken).ConfigureAwait(false);
        }

        return images;
    }
}
