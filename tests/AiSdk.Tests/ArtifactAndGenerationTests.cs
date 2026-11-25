using System.Net;
using System.Text;
using AiSdk.Artifacts;
using AiSdk.AspNetCore;
using AiSdk.Documents;
using AiSdk.Documents.Generation;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.Tests;

public class ArtifactTests
{
    private static Task<GenerateTextResult> RunAsync(MockLanguageModel model, IReadOnlyList<Tool> tools, string conversationId = "c1") =>
        Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(10),
            Context = new Dictionary<string, object?> { ["conversationId"] = conversationId },
        });

    [Fact]
    public async Task AgentCreatesEditsAndReadsArtifacts()
    {
        var store = new InMemoryArtifactStore();
        var tools = ArtifactTools.Create(store, new ArtifactToolOptions { Url = a => $"/artifacts/{a.Id}" });
        var model = new MockLanguageModel()
            .ReturnsToolCall("create_artifact", new { title = "Página de aterrizaje", type = "html", content = "<h1>Hola</h1><p>Precio: $10</p>" })
            .ReturnsToolCall("update_artifact", new { id = "pagina-de-aterrizaje", oldStr = "$10", newStr = "$12" })
            .ReturnsToolCall("update_artifact", new { id = "pagina-de-aterrizaje", oldStr = "no existe", newStr = "x" })
            .ReturnsToolCall("read_artifact", new { id = "pagina-de-aterrizaje", version = 1 })
            .ReturnsText("Listo");

        var result = await RunAsync(model, tools);

        var created = result.Steps[0].ToolResults[0].Output;
        Assert.Equal("/artifacts/pagina-de-aterrizaje", created.GetProperty("url").GetString());
        var latest = await store.GetAsync("pagina-de-aterrizaje", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, latest!.Version);
        Assert.Equal("<h1>Hola</h1><p>Precio: $12</p>", latest.Content);
        Assert.Equal("c1", latest.ConversationId);
        Assert.True(result.Steps[2].ToolResults[0].IsError); // oldStr inexistente
        Assert.Equal("<h1>Hola</h1><p>Precio: $10</p>", result.Steps[3].ToolResults[0].Output.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ArtifactsAreSentToTheUIAsDataParts()
    {
        var store = new InMemoryArtifactStore();
        var model = new MockLanguageModel()
            .ReturnsToolCall("create_artifact", new { title = "Diagrama", type = "mermaid", content = "graph TD; A-->B" })
            .ReturnsText("ok");
        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = ArtifactTools.Create(store),
            StopWhen = StopCondition.StepCountIs(3),
            Context = new Dictionary<string, object?> { ["conversationId"] = "c1" },
        });

        using var output = new MemoryStream();
        await AiSdk.UI.UIMessageStream.WriteAsync(stream.FullStream, output, cancellationToken: TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("\"type\":\"data-artifact\",\"id\":\"diagrama\"", text);
        Assert.Contains("\"content\":\"graph TD; A--", text);
    }

    [Fact]
    public async Task OtherConversationsCannotEditArtifacts()
    {
        var store = new InMemoryArtifactStore();
        await store.SaveAsync(new Artifact { Id = "privado", Title = "x", Type = "markdown", Content = "secreto", ConversationId = "c1" }, TestContext.Current.CancellationToken);
        var model = new MockLanguageModel().ReturnsToolCall("read_artifact", new { id = "privado" }).ReturnsText("ok");

        var result = await RunAsync(model, ArtifactTools.Create(store), conversationId: "c2");

        Assert.True(result.Steps[0].ToolResults[0].IsError);
    }

    [Fact]
    public async Task FileSystemStoreKeepsVersionsAndBinaries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemArtifactStore(dir);
            await store.SaveAsync(new Artifact { Id = "doc", Title = "Doc", Type = ArtifactTypes.Markdown, Content = "v1" }, TestContext.Current.CancellationToken);
            await store.SaveAsync(new Artifact { Id = "doc", Title = "Doc", Type = ArtifactTypes.Markdown, Content = "v2" }, TestContext.Current.CancellationToken);
            await store.SaveAsync(new Artifact { Id = "bin", Title = "Bin", Type = ArtifactTypes.File, Data = [1, 2, 3], FileName = "a.bin" }, TestContext.Current.CancellationToken);

            var reopened = new FileSystemArtifactStore(dir);
            Assert.Equal("v2", (await reopened.GetAsync("doc", cancellationToken: TestContext.Current.CancellationToken))!.Content);
            Assert.Equal("v1", (await reopened.GetAsync("doc", 1, TestContext.Current.CancellationToken))!.Content);
            Assert.Equal([1, 2, 3], (await reopened.GetAsync("bin", cancellationToken: TestContext.Current.CancellationToken))!.Data);
            Assert.Equal(2, (await reopened.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Count);
            await Assert.ThrowsAsync<ArgumentException>(() => reopened.GetAsync("../etc", cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RendererProducesStandalonePages()
    {
        var md = ArtifactRenderer.ToHtml(new Artifact { Id = "a", Title = "T <x>", Type = ArtifactTypes.Markdown, Content = "# Hola </script>" });
        Assert.Contains("marked@12.0.2", md);
        Assert.Contains("<title>T &lt;x&gt;</title>", md);
        Assert.DoesNotContain("Hola </script>", md); // el contenido no puede cerrar el script

        var react = ArtifactRenderer.ToHtml(new Artifact { Id = "r", Title = "R", Type = ArtifactTypes.React, Content = "import { useState } from 'react';\nexport default function App() { return <div/>; }" });
        Assert.DoesNotContain("import {", react);
        Assert.Contains("window.__Artifact = function App()", react);

        Assert.Equal("<p>hola</p>", ArtifactRenderer.ToHtml(new Artifact { Id = "h", Title = "H", Type = ArtifactTypes.Html, Content = "<p>hola</p>" }));
    }

    [Fact]
    public async Task EndpointsServeArtifactsInASandbox()
    {
        var store = new InMemoryArtifactStore();
        await store.SaveAsync(new Artifact { Id = "pagina", Title = "Página", Type = ArtifactTypes.Html, Content = "<b>hola</b>", ConversationId = "c1" }, TestContext.Current.CancellationToken);
        await store.SaveAsync(new Artifact { Id = "informe", Title = "Informe", Type = ArtifactTypes.File, Data = "%PDF-1.7"u8.ToArray(), MediaType = "application/pdf", FileName = "informe.pdf" }, TestContext.Current.CancellationToken);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IArtifactStore>(store);
        await using var app = builder.Build();
        // Acceso abierto explícito: la autorización por defecto se prueba en ArtifactSecurityTests.
        app.MapArtifacts(options: new ArtifactEndpointOptions { AllowAnonymous = true, CanAccess = (_, _) => ValueTask.FromResult(true) });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var page = await client.GetAsync("/artifacts/pagina", TestContext.Current.CancellationToken);
        Assert.Equal("<b>hola</b>", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("sandbox", page.Headers.GetValues("Content-Security-Policy").Single());

        var file = await client.GetAsync("/artifacts/informe", TestContext.Current.CancellationToken);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("informe.pdf", file.Content.Headers.ContentDisposition!.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName);

        Assert.Contains("\"id\":\"pagina\"", await client.GetStringAsync("/artifacts?conversationId=c1", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/artifacts/nada", TestContext.Current.CancellationToken)).StatusCode);
    }
}

public class DocumentGenerationTests
{
    private const string Markdown =
        """
        # Informe de Ventas

        Las **ventas** crecieron un *12 %* en Chile y Perú. Ver [detalle](https://ejemplo.cl/ventas).

        ## Resultados

        | País | Ventas | Meta |
        |:-----|-------:|-----:|
        | Chile | 1.250 | 1.100 |
        | Perú | 980 | 1.000 |

        1. Primer punto
        2. Segundo punto
           - Detalle anidado

        - [x] Revisado

        > Cifras preliminares.

        ```csharp
        var total = 1;
        ```

        \pagebreak

        ## Anexo

        Canción, ñandú, ¿qué? ¡sí! € → ≥ 🚀
        """;

    private static void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator().Validate(package).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public async Task MarkdownToWordIsValidAndRoundTrips()
    {
        var bytes = DocxWriter.FromMarkdown(Markdown, new DocumentStyleOptions { Title = "Ventas", Author = "Equipo", HeaderText = "Confidencial" });

        using (var document = WordprocessingDocument.Open(new MemoryStream(bytes), false))
        {
            AssertValid(document);
            Assert.Equal("Ventas", document.PackageProperties.Title);
            Assert.Single(document.MainDocumentPart!.HyperlinkRelationships);
        }

        var back = await new MarkdownConverter().ConvertAsync(bytes, MediaTypes.Docx, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("# Informe de Ventas", back.Markdown);
        Assert.Contains("## Resultados", back.Markdown);
        Assert.Contains("**ventas**", back.Markdown);
        Assert.Contains("[detalle](https://ejemplo.cl/ventas)", back.Markdown);
        Assert.Contains("| Chile | 1.250 | 1.100 |", back.Markdown.Replace("**", string.Empty));
        Assert.Contains("1. Primer punto\n2. Segundo punto", back.Markdown);
        Assert.Contains("Canción, ñandú", back.Markdown);
    }

    [Fact]
    public async Task MarkdownToPdfHasPagesUnicodeAndHeadings()
    {
        var bytes = PdfWriter.FromMarkdown(Markdown, new DocumentStyleOptions { Title = "Ventas", HeaderText = "Confidencial" });

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes))
        {
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Equal("Ventas", pdf.Information.Title);
            Assert.Contains("Página 1 de 2", pdf.GetPage(1).Text);
            Assert.Contains("Canción,", pdf.GetPage(2).Text);
            Assert.Contains("ñandú,", pdf.GetPage(2).Text);
            Assert.Contains("€", pdf.GetPage(2).Text);
            Assert.Contains(pdf.GetPage(1).GetHyperlinks(), l => l.Uri == "https://ejemplo.cl/ventas");
        }

        // La conversión inversa reconoce los títulos por el tamaño de fuente.
        var back = await new MarkdownConverter().ConvertAsync(bytes, MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("# Informe de Ventas", back.Markdown);
        Assert.Contains("## Resultados", back.Markdown);
    }

    [Fact]
    public void PdfFallsBackForMissingGlyphsAndCustomFonts()
    {
        var bytes = PdfWriter.FromMarkdown("Emoji 🚀 y chino 漢字");
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Contains("Emoji ? y chino ??", pdf.GetPage(1).Text);

        var fonts = PdfFonts.FromSingle(File.ReadAllBytes(DocumentConverterTests.Fixture("DejaVuSans.ttf")));
        var custom = PdfWriter.FromMarkdown("Привет мир", new DocumentStyleOptions { PdfFonts = fonts, PageNumbers = false });
        using var pdf2 = UglyToad.PdfPig.PdfDocument.Open(custom);
        Assert.Contains("Привет", pdf2.GetPage(1).Text);
    }

    private sealed record Venta(string Producto, DateTime Fecha, int Unidades, decimal Monto);

    [Fact]
    public async Task SpreadsheetsHaveTypesFormatsTotalsAndFormulas()
    {
        var bytes = new SpreadsheetBuilder { Title = "Ventas", AllowFormulas = true } // datos de confianza: fórmulas activas
            .AddSheet("Ventas", [new Venta("Café", new DateTime(2026, 3, 15), 10, 1500.5m), new Venta("Té", new DateTime(2026, 4, 1), 4, 980m)], new SheetOptions { Totals = true })
            .AddSheet(
                "Cálculo",
                [new SheetColumn("Item"), new SheetColumn("Precio", ColumnType.Currency), new SheetColumn("Cantidad"), new SheetColumn("Subtotal", ColumnType.Currency), new SheetColumn("Margen")],
                [["A", "1.234,50", 2, "=B2*C2", "12,5%"], ["B", 99.9, 3, "=B3*C3", "8%"]])
            .AddMarkdownTables("## Metas\n\n| País | Meta |\n|---|---|\n| Chile | 1100 |")
            .Build();

        using (var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
        {
            AssertValid(document);
            var sheets = document.WorkbookPart!.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(s => s.Name!.Value).ToList();
            Assert.Equal(["Ventas", "Cálculo", "Metas"], sheets);
            var formulas = document.WorkbookPart.WorksheetParts.SelectMany(p => p.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.CellFormula>()).Select(f => f.Text).ToList();
            Assert.Contains("B2*C2", formulas);
            Assert.Contains("SUM(C2:C3)", formulas);
        }

        var back = await new MarkdownConverter().ConvertAsync(bytes, MediaTypes.Xlsx, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("| Café | 2026-03-15 | 10 | 1500.5 |", back.Markdown);
        Assert.Contains("| Total |  | 14 | 2480.5 |", back.Markdown);
        Assert.Contains("| A | 1234.5 | 2 |  | 0.125 |", back.Markdown); // formato latino y porcentaje interpretados
        Assert.Contains("| Chile | 1100 |", back.Markdown);
    }

    [Fact]
    public async Task AgentsGenerateDownloadableFiles()
    {
        var store = new InMemoryArtifactStore();
        var tools = DocumentTools.Create(new DocumentToolOptions { Store = store, ArtifactOptions = new ArtifactToolOptions { Url = a => $"/artifacts/{a.Id}" } });
        var model = new MockLanguageModel()
            .ReturnsToolCall("create_pdf", new { fileName = "cotización 42", markdown = "# Cotización\n\nTotal: $100", titlePage = true, title = "Cotización" })
            .ReturnsToolCall("create_word_document", new { fileName = "carta", markdown = "Estimado cliente:" })
            .ReturnsToolCall("create_spreadsheet", new
            {
                fileName = "inventario",
                sheets = new[]
                {
                    new
                    {
                        name = "Stock",
                        columns = new object[] { new { header = "Producto" }, new { header = "Precio", type = "currency" }, new { header = "Stock", type = "integer" } },
                        rows = new[] { new object[] { "Mesa", 120.5, 3 }, new object[] { "Silla", 45, 10 } },
                        totals = true,
                    },
                },
            })
            .ReturnsText("Aquí tienes los archivos.");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(5),
            Context = new Dictionary<string, object?> { ["conversationId"] = "c1" },
        });

        Assert.All(result.Steps.Take(3), s => Assert.False(s.ToolResults[0].IsError, s.ToolResults[0].Output.GetRawText()));
        var files = await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["cotización 42.pdf", "carta.docx", "inventario.xlsx"], files.Select(f => f.FileName));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(files[0].Data!, 0, 4));
        Assert.Equal("/artifacts/cotizacion-42", result.Steps[0].ToolResults[0].Output.GetProperty("url").GetString());
        var xlsx = await new MarkdownConverter().ConvertAsync(files[2].Data!, MediaTypes.Xlsx, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("| Total | 165.5 | 13 |", xlsx.Markdown);
    }
}
