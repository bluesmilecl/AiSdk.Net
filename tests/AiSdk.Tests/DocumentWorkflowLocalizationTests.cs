using System.Globalization;
using AiSdk.Artifacts;
using AiSdk.Documents;
using AiSdk.Documents.Converters;
using AiSdk.Documents.Generation;
using AiSdk.Workflows;
using AiSdk.Workflows.Postgres;
using DocumentFormat.OpenXml.Packaging;
using Npgsql;

namespace AiSdk.Tests;

/// <summary>Mensajes en inglés por defecto y en español con una cultura de interfaz "es*" (ensamblado satélite).</summary>
public class DocumentWorkflowLocalizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Ejecuta <paramref name="action"/> con la cultura de interfaz indicada y la restaura al terminar.</summary>
    private static T InCulture<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>Versión asíncrona: la cultura fluye por los await de <paramref name="action"/> (y por los hilos que lanza).</summary>
    private static async Task<T> InCultureAsync<T>(string culture, Func<Task<T>> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return await action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("", "The document 'nota.txt' is 11 bytes and exceeds the maximum of 4 bytes.")]
    [InlineData("fr-FR", "The document 'nota.txt' is 11 bytes and exceeds the maximum of 4 bytes.")]
    [InlineData("es-CL", "El documento 'nota.txt' mide 11 bytes y supera el máximo de 4 bytes.")]
    public async Task DocumentSizeLimitErrorFollowsTheCurrentUICulture(string culture, string expected)
    {
        var converter = new MarkdownConverter(new MarkdownConverterOptions { MaxInputBytes = 4 });

        var error = await InCultureAsync(culture, () =>
            Assert.ThrowsAsync<DocumentTooLargeException>(() => converter.ConvertAsync("hola mundo!"u8.ToArray(), "text/plain", "nota.txt", Ct)));

        Assert.Equal(expected, error.Message);
    }

    [Theory]
    [InlineData("", "1 page(s) or image(s) were not sent to OCR or vision: the document maximum was reached (MaxOcrPages = 1).")]
    [InlineData("es-CL", "1 página(s) o imagen(es) no se enviaron a OCR ni a visión: se alcanzó el máximo del documento (MaxOcrPages = 1).")]
    public async Task OcrPartialResultWarningFollowsTheCurrentUICulture(string culture, string expected)
    {
        // Dos páginas escaneadas y un máximo de una: el resultado parcial lleva el aviso en el idioma del usuario.
        var converter = new MarkdownConverter(new MarkdownConverterOptions
        {
            Ocr = new DocumentConverterTests.FakeOcr("texto reconocido"),
            PdfRasterizer = new ReceiptRasterizer(),
            MaxOcrPages = 1,
        });

        var result = await InCultureAsync(culture, () => converter.ConvertAsync(PdfOcrPipelineTests.MixedPdf(), MediaTypes.Pdf, "mixto.pdf", Ct));

        Assert.Contains(expected, result.Warnings);
    }

    [Theory]
    [InlineData("", "The HTML has more than 20 nesting levels: the deepest content was converted to plain text.")]
    [InlineData("es-CL", "El HTML tiene más de 20 niveles de anidamiento: lo más profundo se convirtió a texto plano.")]
    public async Task WarningsFromTheLargeStackThreadFollowTheCallersCulture(string culture, string expected)
    {
        // El HTML se convierte en un hilo propio con pila grande: el aviso sale igual en la cultura de quien convierte.
        var html = string.Concat(Enumerable.Repeat("<div>", 30)) + "fondo" + string.Concat(Enumerable.Repeat("</div>", 30));
        var converter = new MarkdownConverter(new MarkdownConverterOptions { MaxNestingDepth = 20 });

        var result = await InCultureAsync(culture, () => converter.ConvertAsync(System.Text.Encoding.UTF8.GetBytes(html), MediaTypes.Html, "profundo.html", Ct));

        Assert.Contains(expected, result.Warnings);
    }

    [Theory]
    [InlineData("", "Page 1 of 1", @"^[A-Z][a-z]+ \d{1,2}, \d{4}$")]
    [InlineData("es-CL", "Página 1 de 1", @"^\d{1,2} de [a-z]+ de \d{4}$")]
    public void GeneratedWordDocumentFollowsTheCurrentUICulture(string culture, string footer, string datePattern)
    {
        var bytes = InCulture(culture, () => DocxWriter.FromMarkdown("Hola", new DocumentStyleOptions { Title = "Informe", TitlePage = true }));

        using var document = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = document.MainDocumentPart!;
        Assert.Equal(footer, Assert.Single(main.FooterParts).Footer!.InnerText);
        var byline = main.Document!.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().ElementAt(1).InnerText;
        Assert.Matches(datePattern, byline);
    }

    [Theory]
    [InlineData("", "You can generate downloadable files:", "This is a frame from a video.", "Generates a PDF from Markdown", "You can schedule tasks")]
    [InlineData("es-CL", "Puedes generar archivos descargables:", "Este es un fotograma de un video.", "Genera un PDF a partir de Markdown", "Puedes programar tareas")]
    public void ModelFacingTextsFollowTheCurrentUICulture(string culture, string documentInstructions, string framePrompt, string pdfTool, string taskInstructions)
    {
        var (instructions, frame, tool, tasks) = InCulture(culture, () => (
            DocumentTools.Instructions,
            VideoConverter.FramePrompt,
            DocumentTools.Create(new DocumentToolOptions { Store = new InMemoryArtifactStore() }).Single(t => t.Name == "create_pdf").Description,
            AgentTasks.Instructions));

        Assert.StartsWith(documentInstructions, instructions, StringComparison.Ordinal);
        Assert.StartsWith(framePrompt, frame, StringComparison.Ordinal);
        Assert.StartsWith(pdfTool, tool, StringComparison.Ordinal);
        Assert.StartsWith(taskInstructions, tasks, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "The run 'nope' does not exist.", "Step 'flaky' failed (attempt 1/2): 503", "once on 2026-11-01 09:00 UTC")]
    [InlineData("es-CL", "No existe la ejecución 'nope'.", "Paso 'flaky' falló (intento 1/2): 503", "una vez el 2026-11-01 09:00 UTC")]
    public async Task WorkflowErrorsAndLogsFollowTheCurrentUICulture(string culture, string missingRun, string retryLog, string schedule)
    {
        var attempts = 0;
        var engine = new WorkflowEngine().Register(Workflow.Create<int, int>("flaky", (ctx, n) =>
            ctx.StepAsync("flaky", _ => ++attempts < 2 ? throw new HttpRequestException("503") : Task.FromResult(n), new StepOptions { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(1) })));

        var (missing, run, description) = await InCultureAsync(culture, async () => (
            (await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.CancelAsync("nope", Ct))).Message,
            await engine.StartAsync("flaky", 7, cancellationToken: Ct),
            new OnceTrigger(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)).Describe()));

        Assert.Equal(missingRun, missing);
        Assert.Equal(WorkflowStatus.Completed, run.Status);
        Assert.Contains(run.Logs, l => l.EndsWith(" " + retryLog, StringComparison.Ordinal));
        Assert.Equal(schedule, description);
    }

    [Theory]
    [InlineData("", "Invalid SQL identifier in TablePrefix: 'wf-runs' (only letters, digits and _, not starting with a digit and at most 63 characters).")]
    [InlineData("es-CL", "Identificador SQL inválido en TablePrefix: 'wf-runs' (solo letras, dígitos y _, sin empezar por dígito y con 63 caracteres como máximo).")]
    public async Task PostgresStoreErrorsFollowTheCurrentUICulture(string culture, string expected)
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=postgres"); // no se conecta

        var error = InCulture(culture, () => Assert.Throws<ArgumentException>(() => new PostgresWorkflowStore(dataSource, new PostgresWorkflowStoreOptions { TablePrefix = "wf-" })));

        Assert.StartsWith(expected, error.Message, StringComparison.Ordinal); // ArgumentException agrega el nombre del parámetro
    }

    /// <summary>Rasterizador falso: cada página es la imagen del recibo.</summary>
    private sealed class ReceiptRasterizer : IPdfRasterizer
    {
        public Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, byte[]>>(pages.ToDictionary(p => p, _ => File.ReadAllBytes(DocumentConverterTests.Fixture("receipt.png"))));
    }
}
