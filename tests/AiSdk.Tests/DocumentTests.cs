using System.Text;
using System.Text.Json;
using AiSdk.Documents;
using AiSdk.Mistral;

namespace AiSdk.Tests;

public class DocumentConverterTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>OCR falso: devuelve un texto fijo y cuenta las llamadas.</summary>
    internal sealed class FakeOcr(string text, params string[] mediaTypes) : IOcrEngine
    {
        public int Calls;

        public List<string> MediaTypesSeen { get; } = [];

        public string Name => "fake";

        public bool Supports(string mediaType) => mediaTypes.Length == 0 ? mediaType.StartsWith("image/", StringComparison.Ordinal) : mediaTypes.Contains(mediaType);

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            lock (MediaTypesSeen)
            {
                MediaTypesSeen.Add(request.MediaType);
            }

            return Task.FromResult(new OcrResult([new OcrPage(1, text)]));
        }
    }

    [Fact]
    public async Task WordToMarkdownKeepsStructure()
    {
        var result = await new MarkdownConverter().ConvertFileAsync(Fixture("sample.docx"), TestContext.Current.CancellationToken);

        Assert.Equal("Informe Trimestral", result.Title);
        Assert.Contains("# Informe Trimestral", result.Markdown);
        Assert.Contains("## Detalle", result.Markdown);
        Assert.Contains("Las **ventas** crecieron un 12% en el *tercer trimestre*.", result.Markdown);
        Assert.Contains("- Chile\n- Perú", result.Markdown);
        Assert.Contains("1. Revisar precios\n2. Contratar personal", result.Markdown);
        Assert.Contains("| Región | Ventas | Meta |\n| --- | --- | --- |", result.Markdown);
        Assert.Contains(@"900 \| revisar", result.Markdown);
    }

    [Fact]
    public async Task ExcelToMarkdownTables()
    {
        var result = await new MarkdownConverter().ConvertFileAsync(Fixture("sample.xlsx"), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal("Hoja: Ventas", result.Pages[0].Label);
        Assert.Contains("| Café | 2026-03-15 | 1500.5 |", result.Markdown);
        Assert.Contains("| Total |  | 2480.5 |", result.Markdown); // valor calculado de la fórmula
        Assert.DoesNotContain("secreto", result.Markdown); // hoja oculta

        var limited = await new MarkdownConverter(new() { MaxRowsPerSheet = 1, IncludeHiddenSheets = true }).ConvertFileAsync(Fixture("sample.xlsx"), TestContext.Current.CancellationToken);
        Assert.Contains("secreto", limited.Markdown);
        Assert.Contains("filas más omitidas", limited.Markdown);
        Assert.NotEmpty(limited.Warnings);
    }

    [Fact]
    public async Task PowerPointToMarkdownSlides()
    {
        var result = await new MarkdownConverter().ConvertFileAsync(Fixture("sample.pptx"), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Pages.Count);
        Assert.StartsWith("## Diapositiva 1: Plan 2027", result.Markdown);
        Assert.Contains("- Reducir costos 10%\n  - Proveedores", result.Markdown);
        Assert.Contains("**Notas del orador:** Mencionar el presupuesto aprobado.", result.Markdown);
        Assert.Contains("| TI | 50.000 |", result.Markdown);
    }

    [Fact]
    public async Task PdfTextWithHeadingsAndWithoutRepeatedHeaders()
    {
        var result = await new MarkdownConverter().ConvertFileAsync(Fixture("text.pdf"), TestContext.Current.CancellationToken);

        Assert.Equal("Manual de Usuario", result.Title);
        Assert.Equal(3, result.Pages.Count);
        Assert.All(result.Pages, p => Assert.Equal(PageTextSource.Text, p.Source));
        Assert.StartsWith("# Manual de Usuario", result.Markdown);
        Assert.Contains("## Solución de problemas", result.Markdown);
        Assert.Contains("soporte@ejemplo.cl", result.Markdown);
        Assert.DoesNotContain("confidencial", result.Markdown); // encabezado repetido
        Assert.DoesNotContain("Página 2 de 3", result.Markdown); // pie repetido

        var chunks = result.ToVectorDocuments("manual", maxChars: 400, metadata: new Dictionary<string, string> { ["area"] = "soporte" });
        Assert.Contains(chunks, c => c.Metadata!["page"] == "3" && c.Text.Contains("certificado digital"));
        Assert.All(chunks, c => Assert.Equal("soporte", c.Metadata!["area"]));
        Assert.All(chunks, c => Assert.Equal("text.pdf", c.Metadata!["source"]));
    }

    [Fact]
    public async Task ScannedPdfWithoutOcrWarns()
    {
        var result = await new MarkdownConverter(new() { PdfRasterizer = null }).ConvertFileAsync(Fixture("scanned.pdf"), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("escaneadas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScannedPdfUsesEmbeddedPageImageForOcr()
    {
        var ocr = new FakeOcr("CONTRATO DE ARRIENDO");
        var result = await new MarkdownConverter(new() { Ocr = ocr, PdfRasterizer = null }).ConvertFileAsync(Fixture("scanned.pdf"), TestContext.Current.CancellationToken);

        Assert.Equal("CONTRATO DE ARRIENDO", result.Markdown);
        Assert.Equal(PageTextSource.Ocr, result.Pages[0].Source);
        Assert.True(result.UsedAi);
        Assert.Equal("image/jpeg", Assert.Single(ocr.MediaTypesSeen));
    }

    [Fact]
    public async Task PdfCapableOcrReceivesWholeDocument()
    {
        var ocr = new FakeOcr("texto", MediaTypes.Pdf);
        var result = await new MarkdownConverter(new() { Ocr = ocr }).ConvertFileAsync(Fixture("scanned.pdf"), TestContext.Current.CancellationToken);

        Assert.Equal(1, ocr.Calls);
        Assert.Equal(MediaTypes.Pdf, ocr.MediaTypesSeen[0]);
        Assert.Equal("texto", result.Markdown);
    }

    [Fact]
    public async Task ScannedPdfWithVisionModelTranscribesPage()
    {
        var vision = new MockLanguageModel().ReturnsText("# CONTRATO DE ARRIENDO\n\nEl arrendatario pagará la renta el día cinco.");
        var result = await new MarkdownConverter(new() { VisionModel = vision, PdfRasterizer = null }).ConvertFileAsync(Fixture("scanned.pdf"), TestContext.Current.CancellationToken);

        Assert.StartsWith("# CONTRATO", result.Markdown);
        Assert.Equal(PageTextSource.Vision, result.Pages[0].Source);
        var prompt = vision.Requests[0].Messages[0];
        Assert.IsType<ImagePart>(prompt.Content[1]);
        Assert.Contains("Transcribe", ((TextPart)prompt.Content[0]).Text);
    }

    [Fact]
    public async Task ImageCombinesDescriptionAndOcr()
    {
        var vision = new MockLanguageModel().ReturnsText("Una boleta impresa sobre fondo blanco.");
        var ocr = new FakeOcr("TOTAL: $12.345");
        var result = await new MarkdownConverter(new() { VisionModel = vision, Ocr = ocr }).ConvertFileAsync(Fixture("receipt.png"), TestContext.Current.CancellationToken);

        Assert.Contains("## Imagen: receipt.png", result.Markdown);
        Assert.Contains("Una boleta impresa", result.Markdown);
        Assert.Contains("**Texto detectado (OCR):**\n\nTOTAL: $12.345", result.Markdown);

        var none = await new MarkdownConverter().ConvertFileAsync(Fixture("receipt.png"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(none.Warnings);
    }

    [Fact]
    public async Task TextFormatsAreConverted()
    {
        var converter = new MarkdownConverter();

        var csv = await converter.ConvertAsync(Encoding.UTF8.GetBytes("nombre;nota\n\"Pérez; Ana\";7\nLuis;5\n"), fileName: "notas.csv", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("| nombre | nota |\n| --- | --- |\n| Pérez; Ana | 7 |\n| Luis | 5 |", csv.Markdown);

        var json = await converter.ConvertAsync(Encoding.UTF8.GetBytes("{\"a\":1,\"ñ\":\"sí\"}"), "application/json", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("```json\n{\n  \"a\": 1,\n  \"ñ\": \"sí\"\n}\n```", json.Markdown);

        var html = await converter.ConvertAsync(Encoding.UTF8.GetBytes("<html><head><title>Hola</title><style>p{}</style></head><body><script>alert(1)</script><h1>Título</h1><p>Texto <b>importante</b> y <a href=\"https://x.cl\">enlace</a></p><ul><li>uno</li></ul></body></html>"), fileName: "pagina.html", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Hola", html.Title);
        Assert.Contains("# Título", html.Markdown);
        Assert.Contains("**importante**", html.Markdown);
        Assert.Contains("[enlace](https://x.cl)", html.Markdown);
        Assert.Contains("- uno", html.Markdown);
        Assert.DoesNotContain("alert", html.Markdown);
    }

    [Fact]
    public void MediaTypesAreDetectedFromContent()
    {
        Assert.Equal(MediaTypes.Docx, MediaTypes.Detect(File.ReadAllBytes(Fixture("sample.docx"))));
        Assert.Equal(MediaTypes.Xlsx, MediaTypes.Detect(File.ReadAllBytes(Fixture("sample.xlsx")), "datos.bin"));
        Assert.Equal("image/png", MediaTypes.Detect(File.ReadAllBytes(Fixture("receipt.png")), "sin-extension"));
        Assert.Equal(MediaTypes.Pdf, new DocumentSource(File.ReadAllBytes(Fixture("text.pdf")), "application/octet-stream").MediaType);
        Assert.Equal("video/mp4", MediaTypes.Detect(File.ReadAllBytes(Fixture("clip.mp4"))));
        Assert.Equal(MediaTypes.PlainText, MediaTypes.Detect("hola mundo"u8));
        Assert.Equal("image/jpeg", MediaTypes.Normalize("image/JPG; q=1"));
    }

    [Fact]
    public async Task CustomConvertersTakePriority()
    {
        var converter = new MarkdownConverter().Use(new UpperCaseConverter());
        var result = await converter.ConvertAsync(Encoding.UTF8.GetBytes("hola"), "text/plain", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("HOLA", result.Markdown);
        await Assert.ThrowsAsync<NotSupportedException>(() => converter.ConvertAsync([0, 1, 2, 0], "application/x-desconocido", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestIntoVectorStore()
    {
        var store = new InMemoryVectorStore(new MockEmbeddingModel());
        var result = await store.IngestAsync(new MarkdownConverter(), await DocumentSource.FromFileAsync(Fixture("sample.pptx"), cancellationToken: TestContext.Current.CancellationToken), id: "plan", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Pages.Count);
        var hits = await store.SearchAsync("presupuesto", topK: 10, filter: VectorFilter.Eq("section", "Diapositiva 3"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("plan:p3:c0", Assert.Single(hits).Document.Id);
    }

    [Fact]
    public async Task VisionModelOcrReturnsMarkdown()
    {
        var model = new MockLanguageModel().ReturnsText("(sin texto)").ReturnsText("| a | b |");
        var ocr = new VisionModelOcr(model);
        Assert.Equal(string.Empty, (await ocr.RecognizeAsync(new OcrRequest { Data = [1], MediaType = "image/png" }, TestContext.Current.CancellationToken)).Markdown);
        Assert.Equal("| a | b |", (await ocr.RecognizeAsync(new OcrRequest { Data = [1], MediaType = "image/png" }, TestContext.Current.CancellationToken)).Markdown);
        Assert.Equal(0, model.Requests[0].Temperature);
    }

    [Fact]
    public async Task MistralOcrSendsDocumentAndParsesPages()
    {
        var handler = new FakeHandler().Json("""{"pages":[{"index":0,"markdown":"# Uno"},{"index":1,"markdown":"Dos"}],"model":"mistral-ocr-latest"}""");
        var ocr = new MistralProvider(new MistralProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) }).Ocr();

        var result = await ocr.RecognizeAsync(new OcrRequest { Data = "%PDF-1.4"u8.ToArray(), MediaType = MediaTypes.Pdf }, TestContext.Current.CancellationToken);

        Assert.Equal("# Uno\n\nDos", result.Markdown);
        Assert.Equal(2, result.Pages[1].Number);
        var (request, body) = handler.Requests[0];
        Assert.EndsWith("/v1/ocr", request.RequestUri!.AbsolutePath);
        Assert.Equal("document_url", (string?)body["document"]!["type"]);
        Assert.StartsWith("data:application/pdf;base64,", (string?)body["document"]!["document_url"]);
        Assert.True(ocr.Supports("image/png"));
    }

    [Fact]
    public async Task TesseractReadsImagesAndScannedPdfs()
    {
        var tesseract = new TesseractOcr();
        Assert.SkipUnless(tesseract.IsAvailable, "tesseract no instalado");

        var converter = new MarkdownConverter(new() { Ocr = tesseract, Language = "es" });
        var receipt = await converter.ConvertFileAsync(Fixture("receipt.png"), TestContext.Current.CancellationToken);
        Assert.Contains("TOTAL: $12.345", receipt.Markdown);
        Assert.Contains("Juan Perez", receipt.Markdown);

        var scanned = await converter.ConvertFileAsync(Fixture("scanned.pdf"), TestContext.Current.CancellationToken);
        Assert.Contains("CONTRATO DE ARRIENDO", scanned.Markdown);
        Assert.Contains("renta", scanned.Markdown);
    }

    [Fact]
    public async Task VideoBecomesTimelineWithTranscriptAndFrames()
    {
        var tesseract = new TesseractOcr();
        Assert.SkipUnless(ExternalProcess.Exists("ffmpeg") && tesseract.IsAvailable, "ffmpeg o tesseract no instalados");

        var transcription = new FakeTranscription();
        var converter = new MarkdownConverter(new()
        {
            Ocr = tesseract,
            Transcription = transcription,
            VideoFrameInterval = TimeSpan.FromSeconds(2),
        });
        var result = await converter.ConvertFileAsync(Fixture("clip.mp4"), TestContext.Current.CancellationToken);

        Assert.Equal("audio/mpeg", transcription.MediaType);
        Assert.Contains("Duración: 00:06", result.Markdown);
        Assert.Contains("### [00:00]\n**Escena:** Bienvenidos al curso\n**Audio:** Hola a todos.", result.Markdown);
        Assert.Contains("**Escena:** Modulo 2: Facturas", result.Markdown);
        Assert.Contains("**Audio:** Ahora veremos facturas.", result.Markdown);
        // Fotogramas iguales consecutivos no se repiten.
        Assert.Equal(1, CountOccurrences(result.Markdown, "Bienvenidos al curso"));
    }

    private static int CountOccurrences(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private sealed class FakeTranscription : ITranscriptionModel
    {
        public string? MediaType { get; private set; }

        public string Provider => "fake";

        public string ModelId => "fake";

        public Task<TranscriptionModelResult> TranscribeAsync(TranscriptionModelRequest request, CancellationToken cancellationToken = default)
        {
            MediaType = request.MediaType;
            return Task.FromResult(new TranscriptionModelResult("Hola a todos. Ahora veremos facturas.")
            {
                Segments = [new TranscriptionSegment("Hola a todos.", 0.2, 1.5), new TranscriptionSegment("Ahora veremos facturas.", 4.1, 5.5)],
            });
        }
    }

    private sealed class UpperCaseConverter : IDocumentConverter
    {
        public bool CanConvert(string mediaType) => mediaType == "text/plain";

        public Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(DocumentConversionResult.FromPages(source, [new DocumentPage(1, source.ReadText().ToUpperInvariant())]));
    }
}

public class AttachmentMiddlewareTests
{
    [Fact]
    public async Task AttachmentsBecomeTextForModelsWithoutVision()
    {
        var inner = new MockLanguageModel().ReturnsText("ok").ReturnsText("ok2");
        var ocr = new DocumentConverterTests.FakeOcr("TOTAL: $12.345");
        var converter = new MarkdownConverter(new() { Ocr = ocr });
        var model = inner.WithMiddleware(DocumentMiddlewares.ConvertAttachments(converter));

        var messages = new List<ModelMessage>
        {
            ModelMessage.User(
                new TextPart("¿Cuánto pagué y qué dice el informe?"),
                ImagePart.FromBytes(File.ReadAllBytes(DocumentConverterTests.Fixture("receipt.png"))),
                new FilePart { Data = File.ReadAllBytes(DocumentConverterTests.Fixture("sample.docx")), MediaType = "application/octet-stream", Filename = "informe.docx" }),
        };
        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Messages = messages });

        var sent = inner.Requests[0].Messages[0].Content;
        Assert.All(sent, p => Assert.IsType<TextPart>(p));
        Assert.StartsWith("<document name=\"adjunto\" type=\"image/png\">", ((TextPart)sent[1]).Text);
        Assert.Contains("TOTAL: $12.345", ((TextPart)sent[1]).Text);
        Assert.Contains("<document name=\"informe.docx\" type=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\">", ((TextPart)sent[2]).Text);
        Assert.Contains("# Informe Trimestral", ((TextPart)sent[2]).Text);

        // El segundo turno reutiliza la conversión cacheada.
        messages.Add(ModelMessage.Assistant("ok"));
        messages.Add(ModelMessage.User("¿y la meta del sur?"));
        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Messages = messages });
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task NativeAttachmentsAreKeptAndLongOnesTruncated()
    {
        var inner = new MockLanguageModel().ReturnsText("ok");
        var model = inner.WithMiddleware(DocumentMiddlewares.ConvertAttachments(
            new MarkdownConverter(),
            new AttachmentConversionOptions { Native = NativeAttachments.Images, MaxTokensPerAttachment = 20 }));

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Messages =
            [
                ModelMessage.User(
                    ImagePart.FromBytes([0x89, 0x50, 0x4E, 0x47]),
                    new FilePart { Data = File.ReadAllBytes(DocumentConverterTests.Fixture("text.pdf")), MediaType = MediaTypes.Pdf, Filename = "manual.pdf" }),
            ],
        });

        var sent = inner.Requests[0].Messages[0].Content;
        Assert.IsType<ImagePart>(sent[0]);
        var text = Assert.IsType<TextPart>(sent[1]).Text;
        Assert.Contains("[… contenido recortado por longitud]", text);
        Assert.True(text.Length < 250);
    }

    [Fact]
    public async Task LongAttachmentsAreCompressedByRelevance()
    {
        var inner = new MockLanguageModel().ReturnsText("ok");
        var model = inner.WithMiddleware(DocumentMiddlewares.ConvertAttachments(
            new MarkdownConverter(),
            new AttachmentConversionOptions { MaxTokensPerAttachment = 60, CompressionEmbeddings = new CompressionTests.KeywordEmbeddings() }));

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Messages =
            [
                ModelMessage.User(
                    new FilePart { Data = File.ReadAllBytes(DocumentConverterTests.Fixture("text.pdf")), MediaType = MediaTypes.Pdf, Filename = "manual.pdf" },
                    new TextPart("¿Qué hago si falla el certificado?")),
            ],
        });

        var text = ((TextPart)inner.Requests[0].Messages[0].Content[0]).Text;
        Assert.Contains("certificado digital", text);
        Assert.DoesNotContain("Nueva Factura", text);
    }
}

public class CompressionTests
{
    /// <summary>Embeddings por palabras clave para pruebas deterministas.</summary>
    internal sealed class KeywordEmbeddings : IEmbeddingModel
    {
        private static readonly string[] Vocabulary = ["certificado", "factura", "instalar", "gato", "clima"];

        public string Provider => "test";

        public string ModelId => "kw";

        public int? MaxEmbeddingsPerCall => null;

        public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingModelResult(
                values.Select(v => Vocabulary.Select(w => v.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1f : 0.01f).ToArray()).ToList(),
                null));
    }

    [Fact]
    public void TokenEstimatorApproximatesTokens()
    {
        Assert.Equal(0, TokenEstimator.Estimate((string?)null));
        Assert.Equal(3, TokenEstimator.Estimate("hola mundo!"));
        Assert.Equal(4, TokenEstimator.Estimate("你好世界"));
        Assert.True(TokenEstimator.Estimate(ModelMessage.User(ImagePart.FromBytes([1]))) >= TokenEstimator.AttachmentTokens);
    }

    [Fact]
    public async Task SelectRelevantKeepsChunksAboutTheQuery()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => i == 7
            ? "El certificado digital vence cada año y debe renovarse."
            : $"Párrafo {i} sobre el clima y los gatos del barrio, sin relación."));

        var compressed = await ContextCompressor.SelectRelevantAsync(new KeywordEmbeddings(), text, "¿cuándo vence el certificado?", maxTokens: 20, chunkChars: 80, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("El certificado digital vence cada año y debe renovarse.", compressed);
    }

    [Fact]
    public async Task SummarizeUsesMapReduceForLongTexts()
    {
        // La fase map corre en paralelo: la respuesta depende del fragmento recibido, no del orden de llegada.
        static LanguageModelResult SummaryFor(LanguageModelRequest request) => new()
        {
            Content = [new TextPart(JsonSerializer.Serialize(request.Messages).Contains("aaaa", StringComparison.Ordinal) ? "Resumen A" : "Resumen B")],
            FinishReason = FinishReason.Stop,
            Usage = new Usage { InputTokens = 10, OutputTokens = 5 },
        };
        var model = new MockLanguageModel().Returns(SummaryFor).Returns(SummaryFor);
        var text = new string('a', 300) + "\n\n" + new string('b', 300);

        var summary = await ContextCompressor.SummarizeAsync(model, text, targetTokens: 50, chunkTokens: 80, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("Resumen A\n\nResumen B", summary);
    }

    private static List<ModelMessage> LongConversation(int turns)
    {
        var messages = new List<ModelMessage> { ModelMessage.System("Eres un asistente.") };
        for (var i = 0; i < turns; i++)
        {
            messages.Add(ModelMessage.User($"Pregunta {i}: " + new string('x', 200)));
            messages.Add(ModelMessage.Assistant(new ToolCallPart($"c{i}", "buscar", JsonSerializer.SerializeToElement(new { q = i }))));
            messages.Add(ModelMessage.Tool(new ToolResultPart($"c{i}", "buscar", JsonSerializer.SerializeToElement(new string('r', 3000)))));
            messages.Add(ModelMessage.Assistant($"Respuesta {i}"));
        }

        return messages;
    }

    [Fact]
    public async Task CompressContextSummarizesOldTurnsIncrementally()
    {
        var summarizer = new MockLanguageModel().ReturnsText("- El usuario hizo preguntas 0..").ReturnsText("- Resumen actualizado");
        var inner = new MockLanguageModel().ReturnsText("ok").ReturnsText("ok");
        var model = inner.WithMiddleware(Middlewares.CompressContext(maxInputTokens: 800, summarizer, keepRecentMessages: 3));

        var messages = LongConversation(6);
        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Messages = messages });

        var sent = inner.Requests[0].Messages;
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.StartsWith("Resumen de la conversación anterior:\n- El usuario", sent[1].Text);
        Assert.NotEqual(ChatRole.Tool, sent[2].Role); // nunca empieza con un resultado huérfano
        Assert.Equal("Respuesta 5", sent[^1].Text);
        Assert.True(TokenEstimator.Estimate(sent) <= 800);

        // Siguiente turno: solo se resumen los mensajes nuevos junto al resumen previo.
        messages.AddRange(LongConversation(2).Skip(1));
        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Messages = messages });
        Assert.StartsWith("Resumen previo:\n- El usuario", summarizer.Requests[1].Messages[^1].Text);
        Assert.Equal("Resumen de la conversación anterior:\n- Resumen actualizado", inner.Requests[1].Messages[1].Text);
    }

    [Fact]
    public async Task CompressContextTrimsToolResultsBeforeDropping()
    {
        var inner = new MockLanguageModel().ReturnsText("ok").ReturnsText("ok");
        var model = inner.WithMiddleware(Middlewares.CompressContext(new ContextCompressionOptions { MaxInputTokens = 2500, KeepRecentMessages = 4, MaxToolResultTokens = 50 }));

        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Messages = LongConversation(4) });
        var sent = inner.Requests[0].Messages;
        Assert.Equal(17, sent.Count); // nada descartado, solo recortado
        Assert.Contains("[resultado recortado]", sent[3].Content.OfType<ToolResultPart>().Single().Output.GetString());
        Assert.Equal(3000, sent[^2].Content.OfType<ToolResultPart>().Single().Output.GetString()!.Length); // reciente intacto

        // Sin resumidor y con poco presupuesto se descartan los antiguos con una nota.
        var tight = inner.WithMiddleware(Middlewares.CompressContext(300));
        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = tight, Messages = LongConversation(4) });
        Assert.Contains("Se omitieron", inner.Requests[1].Messages[1].Text);
    }

    [Fact]
    public async Task SmallConversationsAreUntouched()
    {
        var inner = new MockLanguageModel().ReturnsText("ok");
        var model = inner.WithMiddleware(Middlewares.CompressContext(10_000));
        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "hola" });
        Assert.Single(inner.Requests[0].Messages);
    }
}
