using System.Collections.Concurrent;
using System.Text;
using AiSdk.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static AiSdk.Tests.DocumentConverterTests;

namespace AiSdk.Tests;

public class PdfOcrPipelineTests
{
    /// <summary>PDF de 3 páginas: 1 y 3 escaneadas (solo una imagen), 2 con texto.</summary>
    internal static byte[] MixedPdf()
    {
        var png = File.ReadAllBytes(Fixture("receipt.png"));
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddPng(png, new PdfRectangle(50, 300, 545, 800));
        builder.AddPage(595, 842).AddText("Esta pagina tiene texto suficiente para no pasar por OCR.", 12, new PdfPoint(50, 700), font);
        builder.AddPage(595, 842).AddPng(png, new PdfRectangle(50, 300, 545, 800));
        return builder.Build();
    }

    /// <summary>Rasterizador falso: registra cada llamada y la concurrencia máxima; puede fallar en ciertas páginas.</summary>
    private sealed class RecordingRasterizer(params int[] failingPages) : IPdfRasterizer
    {
        private int _active;

        public ConcurrentQueue<(string Path, int[] Pages)> Calls { get; } = new();

        public int MaxActive;

        /// <summary>Sin archivo de origen, el conversor le pasa los bytes (no escribe una copia temporal para él).</summary>
        public Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default) =>
            RenderCoreAsync("(bytes)", pages, cancellationToken);

        public Task<IReadOnlyDictionary<int, byte[]>> RenderFileAsync(string path, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default)
        {
            Assert.True(File.Exists(path));
            return RenderCoreAsync(path, pages, cancellationToken);
        }

        private async Task<IReadOnlyDictionary<int, byte[]>> RenderCoreAsync(string path, IReadOnlyList<int> pages, CancellationToken cancellationToken)
        {
            Calls.Enqueue((path, pages.ToArray()));
            var active = Interlocked.Increment(ref _active);
            InterlockedMax(ref MaxActive, active);
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref _active);
            if (pages.Any(failingPages.Contains))
            {
                throw new InvalidOperationException("página dañada");
            }

            return pages.ToDictionary(p => p, FakePng);
        }

        /// <summary>Firma PNG seguida del número de página.</summary>
        public static byte[] FakePng(int page) => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes($"-{page}")];

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    /// <summary>OCR falso que identifica la imagen recibida (rasterizada: "pagina N"; incrustada: "incrustada").</summary>
    private sealed class EchoOcr : IOcrEngine
    {
        public string Name => "echo";

        public bool Supports(string mediaType) => mediaType.StartsWith("image/", StringComparison.Ordinal);

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            // Las imágenes del rasterizador falso miden 8 bytes de firma + "-N"; las incrustadas, kilobytes.
            var text = request.Data.Length < 16 ? Encoding.ASCII.GetString(request.Data, 8, request.Data.Length - 8) : string.Empty;
            return Task.FromResult(new OcrResult([new OcrPage(1, text.StartsWith('-') ? "pagina " + text[1..] : "incrustada")]));
        }
    }

    private sealed class ProgressLog : IProgress<DocumentConversionProgress>
    {
        public ConcurrentQueue<DocumentConversionProgress> Reports { get; } = new();

        public void Report(DocumentConversionProgress value) => Reports.Enqueue(value);
    }

    [Fact]
    public async Task ScannedPagesAreRasterizedAndRecognizedOneByOne()
    {
        var rasterizer = new RecordingRasterizer();
        var progress = new ProgressLog();
        var converter = new MarkdownConverter(new() { Ocr = new EchoOcr(), PdfRasterizer = rasterizer, MaxConcurrency = 1, Progress = progress });

        var result = await converter.ConvertAsync(MixedPdf(), MediaTypes.Pdf, "mixto.pdf", TestContext.Current.CancellationToken);

        Assert.Equal(["pagina 1", "Esta pagina tiene texto suficiente para no pasar por OCR.", "pagina 3"], result.Pages.Select(p => p.Markdown));
        Assert.Equal([PageTextSource.Ocr, PageTextSource.Text, PageTextSource.Ocr], result.Pages.Select(p => p.Source));
        Assert.Equal([1, 3], rasterizer.Calls.Select(c => Assert.Single(c.Pages)).Order());
        Assert.All(rasterizer.Calls, c => Assert.Equal("(bytes)", c.Path));
        Assert.Equal(1, rasterizer.MaxActive);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            [new DocumentConversionProgress(3, 2, 0), new DocumentConversionProgress(3, 2, 1), new DocumentConversionProgress(3, 2, 2)],
            progress.Reports.OrderBy(r => r.OcrPagesDone));
    }

    /// <summary>Rasterizador que solo implementa la sobrecarga con bytes (como los de terceros).</summary>
    private sealed class BytesOnlyRasterizer : IPdfRasterizer
    {
        public int Calls;

        public Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyDictionary<int, byte[]>>(pages.ToDictionary(p => p, RecordingRasterizer.FakePng));
        }
    }

    [Fact]
    public async Task RasterizersWithoutFileSupportReceiveTheBytesWithoutATemporaryCopy()
    {
        var rasterizer = new BytesOnlyRasterizer();
        var result = await new MarkdownConverter(new() { Ocr = new EchoOcr(), PdfRasterizer = rasterizer })
            .ConvertAsync(MixedPdf(), MediaTypes.Pdf, "mixto.pdf", TestContext.Current.CancellationToken);

        Assert.Equal(2, rasterizer.Calls);
        Assert.Equal(["pagina 1", "pagina 3"], result.Pages.Where(p => p.Source == PageTextSource.Ocr).Select(p => p.Markdown));
    }

    [Fact]
    public async Task RasterizerReadsTheOriginalFileWhenTheSourceComesFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "aisdk-mixto-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, MixedPdf(), TestContext.Current.CancellationToken);
        try
        {
            var rasterizer = new RecordingRasterizer();
            var converter = new MarkdownConverter(new() { Ocr = new EchoOcr(), PdfRasterizer = rasterizer, MaxConcurrency = 2 });

            var result = await converter.ConvertFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(path), (await DocumentSource.FromFileAsync(path, cancellationToken: TestContext.Current.CancellationToken)).FilePath);
            Assert.All(rasterizer.Calls, c => Assert.Equal(Path.GetFullPath(path), c.Path));
            Assert.Equal(2, rasterizer.Calls.Count);
            Assert.InRange(rasterizer.MaxActive, 1, 2);
            Assert.Equal("pagina 3", result.Pages[2].Markdown);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PageThatCannotBeRasterizedFallsBackToItsEmbeddedImage()
    {
        var converter = new MarkdownConverter(new() { Ocr = new EchoOcr(), PdfRasterizer = new RecordingRasterizer(failingPages: 3) });

        var result = await converter.ConvertAsync(MixedPdf(), MediaTypes.Pdf, "mixto.pdf", TestContext.Current.CancellationToken);

        Assert.Equal("pagina 1", result.Pages[0].Markdown);
        Assert.Equal("incrustada", result.Pages[2].Markdown);
        Assert.Contains(result.Warnings, w => w.StartsWith("1 página(s) no se pudieron rasterizar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutRasterizerOnlyScannedPagesHaveTheirImagesRead()
    {
        var ocr = new FakeOcr("reconocido");
        var progress = new ProgressLog();
        var result = await new MarkdownConverter(new() { Ocr = ocr, PdfRasterizer = null, Progress = progress })
            .ConvertAsync(MixedPdf(), MediaTypes.Pdf, "mixto.pdf", TestContext.Current.CancellationToken);

        Assert.Equal(2, ocr.Calls);
        Assert.Equal(["reconocido", "reconocido"], result.Pages.Where(p => p.Source == PageTextSource.Ocr).Select(p => p.Markdown));
        Assert.Equal(new DocumentConversionProgress(3, 2, 2), progress.Reports.MaxBy(r => r.OcrPagesDone));
    }

    [Fact]
    public async Task PdftoppmRendersPageRangesFromAFileAndSkipsPagesThatFail()
    {
        var pdftoppm = PdftoppmRasterizer.TryCreate();
        Assert.SkipWhen(pdftoppm is null, "pdftoppm no instalado");
        var path = Path.Combine(Path.GetTempPath(), "aisdk-mixto-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, MixedPdf(), TestContext.Current.CancellationToken);
        try
        {
            var rasterizer = new PdftoppmRasterizer { MaxPagesPerProcess = 2, MaxParallelism = 2 };
            var images = await rasterizer.RenderFileAsync(path, [3, 1, 2, 2], dpi: 50, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal([1, 2, 3], images.Keys.Order());
            Assert.All(images.Values, png => Assert.Equal("image/png", MediaTypes.Detect(png)));

            // La página 9 no existe: se omite y las demás se devuelven igual.
            images = await rasterizer.RenderFileAsync(path, [1, 9], dpi: 50, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal([1], images.Keys);

            // Si no se pudo ninguna, se informa el error.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rasterizer.RenderFileAsync(path, [9], dpi: 50, cancellationToken: TestContext.Current.CancellationToken));

            // La sobrecarga con bytes sigue disponible.
            var fromBytes = await rasterizer.RenderAsync(MixedPdf(), [2], dpi: 50, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal([2], fromBytes.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ExternalProcessRunnerTests
{
    /// <summary>Ejecutor falso: registra lo que se le pide ejecutar.</summary>
    private sealed class FakeRunner : IExternalProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public bool Exists(string executable) => true;

        public Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((executable, arguments));
            }

            var output = arguments.Contains("--list-langs") ? "List of available languages:\nspa\neng\n" : "texto reconocido";
            return Task.FromResult(new ExternalProcess.Result(0, Encoding.UTF8.GetBytes(output), string.Empty));
        }
    }

    [Fact]
    public void DefaultGlobalRunnerLaunchesProcessesDirectly() =>
        Assert.IsType<ExternalProcessRunner>(ExternalProcess.Runner);

    [Fact]
    public async Task ComponentsUseTheRunnerTheyReceive()
    {
        var runner = new FakeRunner();
        var tesseract = new TesseractOcr(executable: "mi-tesseract", runner: runner);

        Assert.True(tesseract.IsAvailable);
        var result = await tesseract.RecognizeAsync(new OcrRequest { Data = [1, 2, 3], MediaType = "image/png", Language = "es" }, TestContext.Current.CancellationToken);

        Assert.Equal("texto reconocido", result.Markdown);
        Assert.All(runner.Calls, c => Assert.Equal("mi-tesseract", c.Executable));
        Assert.Equal(["stdin", "stdout", "-l", "spa+eng"], runner.Calls[^1].Arguments);
        Assert.NotNull(PdftoppmRasterizer.TryCreate("pdftoppm-falso", runner));
    }

    [Fact]
    public void BuildsTheCommandWithWrapperAndFilteredEnvironment()
    {
        Environment.SetEnvironmentVariable("AISDK_TEST_SECRETO", "no-debe-pasar");
        Environment.SetEnvironmentVariable("AISDK_SANDBOX_MEM", "512");
        var runner = new ExternalProcessRunner(new ExternalProcessOptions
        {
            Wrapper = "/usr/local/bin/aislar",
            WrapperArguments = ["--sin-red"],
            AllowedEnvironmentVariables = ["PATH", "AISDK_SANDBOX_*"],
            EnvironmentVariables = new Dictionary<string, string?> { ["OMP_THREAD_LIMIT"] = "1", ["PATH"] = null },
            WorkingDirectory = Path.GetTempPath(),
        });

        var info = runner.CreateStartInfo("tesseract", ["stdin", "stdout"], redirectInput: true);

        Assert.Equal("/usr/local/bin/aislar", info.FileName);
        Assert.Equal(["--sin-red", "tesseract", "stdin", "stdout"], info.ArgumentList);
        Assert.True(info.RedirectStandardInput);
        Assert.Equal(Path.GetTempPath(), info.WorkingDirectory);
        Assert.Equal("512", info.Environment["AISDK_SANDBOX_MEM"]);
        Assert.Equal("1", info.Environment["OMP_THREAD_LIMIT"]);
        Assert.False(info.Environment.ContainsKey("AISDK_TEST_SECRETO"));
        Assert.False(info.Environment.ContainsKey("PATH")); // un valor null la quita
        Assert.False(runner.Exists("tesseract")); // el envoltorio no existe

        // Envoltorio que ejecuta las herramientas en otro lugar: solo se comprueba el envoltorio.
        var remote = new ExternalProcessRunner(new ExternalProcessOptions { Wrapper = "sh", CheckExecutable = false });
        Assert.True(remote.Exists("herramienta-que-no-esta-aqui"));
        Assert.False(new ExternalProcessRunner(new ExternalProcessOptions { Wrapper = "sh" }).Exists("herramienta-que-no-esta-aqui"));
    }

    [Fact]
    public void ToolsRunWithOneOpenMpThreadUnlessConfigured()
    {
        // Tesseract con OpenMP y varias páginas en paralelo: sin este límite, 4 páginas tardaban minutos en vez de segundos.
        Assert.Equal("1", new ExternalProcessRunner(new ExternalProcessOptions { AllowedEnvironmentVariables = [] })
            .CreateStartInfo("tesseract", []).Environment["OMP_THREAD_LIMIT"]);
        Assert.Equal("4", new ExternalProcessRunner(new ExternalProcessOptions { AllowedEnvironmentVariables = [], EnvironmentVariables = new Dictionary<string, string?> { ["OMP_THREAD_LIMIT"] = "4" } })
            .CreateStartInfo("tesseract", []).Environment["OMP_THREAD_LIMIT"]);
        Assert.False(new ExternalProcessRunner(new ExternalProcessOptions { AllowedEnvironmentVariables = [], LimitOpenMpThreads = false })
            .CreateStartInfo("tesseract", []).Environment.ContainsKey("OMP_THREAD_LIMIT"));
    }

    [Fact]
    public async Task RunsThroughTheWrapperWithOnlyTheAllowedVariables()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "usa sh y env");
        Environment.SetEnvironmentVariable("AISDK_TEST_SECRETO", "no-debe-pasar");
        var runner = new ExternalProcessRunner(new ExternalProcessOptions
        {
            Wrapper = "env",
            WrapperArguments = ["SALUDO=hola"],
            AllowedEnvironmentVariables = ExternalProcessOptions.CommonEnvironmentVariables,
            EnvironmentVariables = new Dictionary<string, string?> { ["OMP_THREAD_LIMIT"] = "1" },
        });

        var output = await runner.RunCheckedAsync("sh", ["-c", "echo \"$SALUDO|$OMP_THREAD_LIMIT|${AISDK_TEST_SECRETO:-vacio}\""], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hola|1|vacio", Encoding.UTF8.GetString(output).Trim());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunCheckedAsync("sh", ["-c", "echo malo >&2; exit 3"], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("código 3: malo", failure.Message, StringComparison.Ordinal);
    }
}
