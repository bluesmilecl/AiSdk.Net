using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiSdk.Documents;
using AiSdk.Documents.Converters;

namespace AiSdk.Tests;

/// <summary>Regresiones de seguridad de AiSdk.Documents: cada prueba construye la entrada hostil y comprueba que el trabajo queda acotado.</summary>
public class HtmlSecurityTests
{
    private static Task<DocumentConversionResult> ConvertHtmlAsync(string html, MarkdownConverterOptions? options = null) =>
        new MarkdownConverter(options).ConvertAsync(Encoding.UTF8.GetBytes(html), MediaTypes.Html, "pagina.html", TestContext.Current.CancellationToken);

    [Fact]
    public async Task DeeplyNestedHtmlDoesNotOverflowTheStack()
    {
        // ~330 KB: antes, ReverseMarkdown recorría el árbol de forma recursiva y el proceso moría (StackOverflowException).
        var html = string.Concat(Enumerable.Repeat("<div>", 30_000)) + "texto del fondo" + string.Concat(Enumerable.Repeat("</div>", 30_000));
        var watch = Stopwatch.StartNew();

        var result = await ConvertHtmlAsync(html);

        Assert.Contains("texto del fondo", result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("niveles de anidamiento", StringComparison.Ordinal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}.");
    }

    [Fact]
    public void ConversionDoesNotDependOnTheCallersStackSize()
    {
        // Un hilo con pila pequeña (como los de algunos hosts) convierte HTML anidado bajo el máximo sin desbordarse.
        var html = string.Concat(Enumerable.Repeat("<blockquote><ul><li>", 100)) + "hondo";
        string? markdown = null;
        var thread = new Thread(() => markdown = HtmlConverter.ToMarkdown(html), 256 * 1024);
        thread.Start();
        thread.Join();

        Assert.Contains("hondo", markdown);
    }

    [Fact]
    public async Task NestingBeyondTheLimitIsFlattenedToText()
    {
        var html = "<h1>Inicio</h1>" + string.Concat(Enumerable.Repeat("<div>", 50)) + "<p>uno</p><p><b>dos</b></p>" + string.Concat(Enumerable.Repeat("</div>", 50)) + "<p>Fin</p>";

        var result = await ConvertHtmlAsync(html, new MarkdownConverterOptions { MaxNestingDepth = 20 });

        Assert.Contains("# Inicio", result.Markdown);
        Assert.Contains("uno", result.Markdown);
        Assert.Contains("dos", result.Markdown);
        Assert.DoesNotContain("**dos**", result.Markdown); // por debajo del máximo queda solo el texto
        Assert.Contains("Fin", result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("más de 20 niveles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MisnestedTagsThatDeepenTheTreeFallBackToText()
    {
        // La reparación de etiquetas mal anidadas (adoption agency) hace crecer el árbol sin que el texto lo parezca.
        var html = "<p>visible</p>" + string.Concat(Enumerable.Repeat("<span><div></span>", 20_000)) + "final";
        var watch = Stopwatch.StartNew();

        var result = await ConvertHtmlAsync(html);

        Assert.Contains("visible", result.Markdown);
        Assert.Contains("final", result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("se extrajo solo el texto", StringComparison.Ordinal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}.");
    }

    [Fact]
    public async Task RepeatedUnclosedScriptsAreRemovedQuickly()
    {
        // 256 KB de <script> sin cerrar: la expresión regular anterior tardaba ~40 s.
        var html = "<html><head><title>T &amp; C</title></head><body><p>antes</p>" + string.Concat(Enumerable.Repeat("<script>x", 28_000)) + "</body></html>";
        var styles = "<p>texto</p>" + string.Concat(Enumerable.Repeat("<style>a{}<style>", 15_000));
        var watch = Stopwatch.StartNew();

        var result = await ConvertHtmlAsync(html);
        var styled = await ConvertHtmlAsync(styles);

        Assert.Equal("T & C", result.Title);
        Assert.Equal("antes", result.Markdown);
        Assert.Equal("texto", styled.Markdown);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}.");
    }

    [Fact]
    public async Task NoiseIsRemovedThroughTheDomAndStructureIsKept()
    {
        var html = """
            <html><head><title>Hola</title><style>p{}</style></head>
            <body><nav><a href="/x">menú</a></nav><script>alert(1)</script><svg><text>dibujo</text></svg>
            <h2>Título</h2><table><tr><th>a</th><th>b</th></tr><tr><td>1</td><td>2 &lt;3&gt;</td></tr></table>
            <p data-x='"q"'>Texto con <a href="https://x.cl/?a=1&amp;b=2">enlace</a></p><footer>pie</footer></body></html>
            """;

        var result = await ConvertHtmlAsync(html);

        Assert.Equal("Hola", result.Title);
        Assert.Contains("## Título", result.Markdown);
        Assert.Contains("| a | b |", result.Markdown);
        Assert.Contains("[enlace](https://x.cl/?a=1&b=2)", result.Markdown);
        Assert.DoesNotContain("menú", result.Markdown);
        Assert.DoesNotContain("alert", result.Markdown);
        Assert.DoesNotContain("dibujo", result.Markdown);
        Assert.DoesNotContain("pie", result.Markdown);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task HtmlBeyondTheSizeLimitIsTruncatedWithAWarning()
    {
        var html = "<p>primero</p>" + new string('x', 5000) + "<p>ultimo</p>";

        var result = await ConvertHtmlAsync(html, new MarkdownConverterOptions { MaxHtmlBytes = 1000 });

        Assert.Contains("primero", result.Markdown);
        Assert.DoesNotContain("ultimo", result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("MaxHtmlBytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextOnlyFallbackIsLinearAndDecodesEntities()
    {
        // Más de 4 × MaxNestingDepth niveles: no se construye el árbol, se extrae el texto en una pasada.
        var html = "<p>a &amp; b</p><script>no</script>< c <!-- x -->" + string.Concat(Enumerable.Repeat("<div>", 2000)) + "d" + new string('<', 200_000);
        var watch = Stopwatch.StartNew();

        var result = await ConvertHtmlAsync(html);

        Assert.StartsWith("a & b\n< c", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("d", result.Markdown);
        Assert.DoesNotContain("no", result.Markdown);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}.");
    }
}

public class LimitTests
{
    [Fact]
    public async Task DocumentsLargerThanMaxInputBytesAreRejectedBeforeConverting()
    {
        var converter = new MarkdownConverter(new MarkdownConverterOptions { MaxInputBytes = 100 });
        var data = Encoding.UTF8.GetBytes(new string('a', 101));

        var error = await Assert.ThrowsAsync<DocumentTooLargeException>(() => converter.ConvertAsync(data, "text/plain", "grande.txt", TestContext.Current.CancellationToken));
        Assert.Equal(101, error.Size);
        Assert.Equal(100, error.Limit);

        var path = Path.Combine(Path.GetTempPath(), "aisdk-grande-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<DocumentTooLargeException>(() => converter.ConvertFileAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }

        await Assert.ThrowsAsync<DocumentTooLargeException>(() =>
            DocumentSource.FromStreamAsync(new MemoryStream(data), maxBytes: 100, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OneVeryWideCsvLineDoesNotMultiplyTheOutput()
    {
        // Una línea con un millón de separadores y 1000 filas de un valor: antes, 1000 × 1 millón de celdas.
        var csv = new StringBuilder("h").Append(',', 1_000_000).Append('\n');
        for (var i = 0; i < 1000; i++)
        {
            csv.Append("v\n");
        }

        var result = await new MarkdownConverter().ConvertAsync(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "ancho.csv", TestContext.Current.CancellationToken);

        Assert.True(result.Markdown.Length < 10_000_000, $"Markdown de {result.Markdown.Length} caracteres.");
        Assert.Contains(result.Warnings, w => w.Contains("columnas", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("celdas", StringComparison.Ordinal));
    }

    [Fact]
    public void MarkdownTableHonoursTheCellBudget()
    {
        IReadOnlyList<IReadOnlyList<string>> rows = [["a", "b", "c"], .. Enumerable.Range(0, 100).Select(i => (IReadOnlyList<string>)[$"{i}"])];

        var table = MarkdownTable.Build(rows, 30, out var truncated);

        Assert.True(truncated);
        Assert.Equal(10 + 1, table.Split('\n').Length); // 10 filas (encabezado incluido) y la línea de separación
    }
}

/// <summary>Servidor HTTP/1.1 mínimo en 127.0.0.1 que registra las rutas pedidas.</summary>
internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, Task<byte[]>> _respond;
    private readonly Task _loop;

    public LocalHttpServer(Func<string, Task<byte[]>> respond)
    {
        _respond = respond;
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

    public Uri Url(string path, string host = "127.0.0.1") => new($"http://{host}:{Port}{path}");

    public static byte[] Response(int status, string body, params string[] headers) =>
        Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{string.Concat(headers.Select(h => h + "\r\n"))}\r\n")
            .Concat(Encoding.UTF8.GetBytes(body)).ToArray();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(System.Net.Sockets.TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var total = 0;
                string head;
                do
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    total += read;
                    head = Encoding.ASCII.GetString(buffer, 0, total);
                }
                while (!head.Contains("\r\n\r\n", StringComparison.Ordinal) && total < buffer.Length);

                var path = head.Split(' ')[1];
                Paths.Enqueue(path);
                await stream.WriteAsync(await _respond(path), _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}

public class UrlDownloadSecurityTests
{
    private static readonly UrlDownloadOptions LoopbackOnly = new() { AddressValidator = ip => ip.Equals(System.Net.IPAddress.Loopback) };

    private static LocalHttpServer Server() => new(async path =>
    {
        await Task.Yield();
        return path switch
        {
            "/doc.txt" => LocalHttpServer.Response(200, "contenido interno", "Content-Type: text/plain"),
            "/start" => LocalHttpServer.Response(302, string.Empty, "Location: /redirect-to-127.0.0.2"),
            "/loop" => LocalHttpServer.Response(302, string.Empty, "Location: /loop"),
            "/ok" => LocalHttpServer.Response(301, string.Empty, "Location: /final"),
            "/final" => LocalHttpServer.Response(200, "hola"),
            "/big" => LocalHttpServer.Response(200, new string('x', 2000)),
            "/chunked" => Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" + string.Concat(Enumerable.Repeat("258\r\n" + new string('y', 600) + "\r\n", 3)) + "0\r\n\r\n"),
            "/slow" => await SlowAsync(),
            _ => LocalHttpServer.Response(404, "no"),
        };
    });

    private static async Task<byte[]> SlowAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        return LocalHttpServer.Response(200, "tarde");
    }

    [Fact]
    public async Task LoopbackPrivateAndMetadataAddressesAreBlockedByDefault()
    {
        await using var server = Server();
        Uri[] targets =
        [
            server.Url("/doc.txt"),
            server.Url("/doc.txt", "localhost"),
            server.Url("/doc.txt", "[::ffff:127.0.0.1]"),
            server.Url("/doc.txt", "[::1]"),
            server.Url("/doc.txt", "2130706433"), // 127.0.0.1 en decimal
            new("http://169.254.169.254/latest/meta-data/iam/"),
            new("http://10.1.2.3/"),
            new("http://100.64.0.1/"),
            new("http://[fd00::1]/"),
            new("http://0.0.0.0/"),
        ];

        foreach (var target in targets)
        {
            var error = await Assert.ThrowsAsync<UrlDownloadException>(() => UrlDownloader.DownloadAsync(target, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("no pública", error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(server.Paths); // ninguna petición llegó al servicio "interno"
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/archivo.pdf")]
    [InlineData("gopher://example.com/")]
    public async Task OnlyHttpAndHttpsAreDownloaded(string url)
    {
        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => UrlDownloader.DownloadAsync(new Uri(url), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("http o https", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectsAreRevalidatedOnEveryHop()
    {
        // Solo 127.0.0.1 está permitido (hace de "Internet"); la redirección lleva a otra dirección de loopback.
        await using var redirecting = new LocalHttpServer(path => Task.FromResult(LocalHttpServer.Response(302, string.Empty, "Location: http://127.0.0.2/latest/meta-data/")));
        var error = await Assert.ThrowsAsync<UrlDownloadException>(() => UrlDownloader.DownloadAsync(redirecting.Url("/start"), LoopbackOnly, TestContext.Current.CancellationToken));
        Assert.Contains("127.0.0.2", error.Message, StringComparison.Ordinal);
        Assert.Equal(["/start"], redirecting.Paths);

        // El validador de URL también se aplica en cada salto.
        await using var server = Server();
        var noRedirectTarget = new UrlDownloadOptions { AddressValidator = LoopbackOnly.AddressValidator, UrlValidator = u => u.AbsolutePath != "/redirect-to-127.0.0.2" };
        await Assert.ThrowsAsync<UrlDownloadException>(() => UrlDownloader.DownloadAsync(server.Url("/start"), noRedirectTarget, TestContext.Current.CancellationToken));
        Assert.Equal(["/start"], server.Paths);

        // Redirección relativa válida.
        var ok = await UrlDownloader.DownloadAsync(server.Url("/ok"), LoopbackOnly, TestContext.Current.CancellationToken);
        Assert.Equal("hola", Encoding.UTF8.GetString(ok.Data));
        Assert.Equal("/final", ok.Url.AbsolutePath);

        var loop = await Assert.ThrowsAsync<UrlDownloadException>(() => UrlDownloader.DownloadAsync(server.Url("/loop"), LoopbackOnly, TestContext.Current.CancellationToken));
        Assert.Contains("redirecciones", loop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadSizeIsCheckedWhileStreamingAndSlowServersTimeOut()
    {
        await using var server = Server();
        var small = new UrlDownloadOptions { AddressValidator = LoopbackOnly.AddressValidator, MaxBytes = 1000, Timeout = TimeSpan.FromMilliseconds(500) };

        await Assert.ThrowsAsync<DocumentTooLargeException>(() => UrlDownloader.DownloadAsync(server.Url("/big"), small, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DocumentTooLargeException>(() => UrlDownloader.DownloadAsync(server.Url("/chunked"), small, TestContext.Current.CancellationToken));

        var watch = Stopwatch.StartNew();
        var timeout = await Assert.ThrowsAsync<UrlDownloadException>(() => UrlDownloader.DownloadAsync(server.Url("/slow"), small, TestContext.Current.CancellationToken));
        Assert.Contains("tiempo máximo", timeout.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        var text = await UrlDownloader.DownloadAsync(server.Url("/doc.txt"), small, TestContext.Current.CancellationToken);
        Assert.Equal("text/plain", text.MediaType);
    }

    [Fact]
    public async Task ConverterAndMiddlewareUseTheSafeDownloader()
    {
        await using var server = Server();
        var part = new FilePart { Url = server.Url("/doc.txt"), MediaType = "text/plain" };

        // Por defecto, la URL de loopback se rechaza (también desde el middleware de adjuntos).
        await Assert.ThrowsAsync<UrlDownloadException>(() => new MarkdownConverter().ConvertAsync(part, downloadUrls: true, TestContext.Current.CancellationToken));
        var model = new MockLanguageModel().ReturnsText("ok").WithMiddleware(DocumentMiddlewares.ConvertAttachments(new MarkdownConverter(), new AttachmentConversionOptions { DownloadUrls = true }));
        await Assert.ThrowsAsync<UrlDownloadException>(() => Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Messages = [ModelMessage.User(part)] }));
        Assert.Empty(server.Paths);

        // Con una opción explícita (servicio interno de confianza) sí se descarga.
        var trusted = new MarkdownConverter(new MarkdownConverterOptions { UrlDownload = new UrlDownloadOptions { AllowPrivateNetworks = true } });
        var result = await trusted.ConvertAsync(part, downloadUrls: true, TestContext.Current.CancellationToken);
        Assert.Equal("contenido interno", result.Markdown);
        Assert.Equal("doc.txt", result.FileName);

        // Las URL data: respetan el mismo máximo.
        var limited = new MarkdownConverter(new MarkdownConverterOptions { UrlDownload = new UrlDownloadOptions { MaxBytes = 20 } });
        var data = new FilePart { Url = new Uri("data:text/plain;base64," + Convert.ToBase64String(new byte[30])), MediaType = "text/plain" };
        await Assert.ThrowsAsync<DocumentTooLargeException>(() => limited.ConvertAsync(data, downloadUrls: true, TestContext.Current.CancellationToken));
        var tiny = await limited.ConvertAsync(new FilePart { Url = new Uri("data:text/plain,hola%20mundo"), MediaType = "text/plain" }, downloadUrls: true, TestContext.Current.CancellationToken);
        Assert.Equal("hola mundo", tiny.Markdown);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)] // NAT64 de 169.254.169.254
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:7f00:1::1", false)] // 6to4
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:0::1", false)] // Teredo
    public void PublicAddressesAreRecognized(string address, bool expected) =>
        Assert.Equal(expected, UrlDownloader.IsPublicAddress(System.Net.IPAddress.Parse(address)));
}

public class OfficeSecurityTests
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static void Write(DocumentFormat.OpenXml.Packaging.OpenXmlPart part, string xml)
    {
        using var stream = part.GetStream(FileMode.Create);
        stream.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml));
    }

    /// <summary>DOCX mínimo con el cuerpo indicado (XML crudo).</summary>
    internal static byte[] Docx(string body)
    {
        using var output = new MemoryStream();
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(output, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            Write(document.AddMainDocumentPart(), $"<w:document xmlns:w=\"{WordNs}\"><w:body>{body}</w:body></w:document>");
        }

        return output.ToArray();
    }

    /// <summary>XLSX mínimo con una hoja (filas en XML crudo) y, opcionalmente, cadenas compartidas.</summary>
    internal static byte[] Xlsx(string sheetData, string? sharedStrings = null)
    {
        using var output = new MemoryStream();
        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(output, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            var sheet = workbook.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
            Write(sheet, $"<worksheet xmlns=\"{SheetNs}\"><sheetData>{sheetData}</sheetData></worksheet>");
            Write(workbook, $"<workbook xmlns=\"{SheetNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Datos\" sheetId=\"1\" r:id=\"{workbook.GetIdOfPart(sheet)}\"/></sheets></workbook>");
            if (sharedStrings is not null)
            {
                Write(workbook.AddNewPart<DocumentFormat.OpenXml.Packaging.SharedStringTablePart>(), $"<sst xmlns=\"{SheetNs}\">{sharedStrings}</sst>");
            }
        }

        return output.ToArray();
    }

    private static Task<DocumentConversionResult> ConvertAsync(byte[] data, string mediaType, MarkdownConverterOptions? options = null) =>
        new MarkdownConverter(options).ConvertAsync(data, mediaType, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task HugeGridSpanIsCappedToWordsColumnLimit()
    {
        // ~1 KB: antes, gridSpan = int.MaxValue agregaba 2.000 millones de celdas vacías.
        var docx = Docx($"<w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"{int.MaxValue}\"/></w:tcPr><w:p><w:r><w:t>ancha</w:t></w:r></w:p></w:tc></w:tr>"
                        + "<w:tr><w:tc><w:p><w:r><w:t>a</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>b</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");

        var result = await ConvertAsync(docx, MediaTypes.Docx);

        var header = result.Markdown.Split('\n')[0];
        Assert.StartsWith("| ancha |", header, StringComparison.Ordinal);
        Assert.Equal(DocxConverter.MaxTableColumns + 1, header.Count(c => c == '|'));
        Assert.Contains("| a | b |", result.Markdown);
    }

    [Fact]
    public async Task ListLevelsAreClampedInWordAndPowerPoint()
    {
        var docx = Docx("<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>uno</w:t></w:r></w:p>"
                        + "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"2000000000\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>profundo</w:t></w:r></w:p>");
        var word = await ConvertAsync(docx, MediaTypes.Docx);
        Assert.Equal("- uno\n" + new string(' ', 16) + "- profundo", word.Markdown);

        // PowerPoint: se modifica la presentación de ejemplo con un nivel de viñeta enorme.
        using var pptx = new MemoryStream();
        await using (var fixture = File.OpenRead(DocumentConverterTests.Fixture("sample.pptx")))
        {
            await fixture.CopyToAsync(pptx, TestContext.Current.CancellationToken);
        }

        using (var presentation = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(pptx, true))
        {
            var slide = presentation.PresentationPart!.SlideParts.SelectMany(s => s.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>()).First(p => p.InnerText.Contains("Proveedores", StringComparison.Ordinal));
            slide.ParagraphProperties ??= new DocumentFormat.OpenXml.Drawing.ParagraphProperties();
            slide.ParagraphProperties.Level = 2_000_000_000;
        }

        var slides = await ConvertAsync(pptx.ToArray(), MediaTypes.Pptx);
        Assert.Contains(new string(' ', 16) + "- Proveedores", slides.Markdown);
        Assert.DoesNotContain(new string(' ', 17) + "- Proveedores", slides.Markdown);
    }

    [Fact]
    public async Task DeeplyNestedOfficeXmlIsRejectedInsteadOfOverflowingTheStack()
    {
        // ~30 KB: el SDK de Open XML carga las partes de forma recursiva; 5000 controles anidados mataban el proceso.
        var docx = Docx(string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 5000)) + "<w:p><w:r><w:t>x</w:t></w:r></w:p>" + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 5000)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConvertAsync(docx, MediaTypes.Docx));
        Assert.Contains("MaxNestingDepth", error.Message, StringComparison.Ordinal);

        var xlsx = Xlsx("<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row>", "<si>" + string.Concat(Enumerable.Repeat("<r>", 5000)) + "</si>");
        await Assert.ThrowsAsync<InvalidDataException>(() => ConvertAsync(xlsx, MediaTypes.Xlsx));

        // Anidamiento normal (controles de contenido dentro de controles) sigue funcionando.
        var nested = Docx(string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 10)) + "<w:p><w:r><w:t>dentro</w:t></w:r></w:p>" + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 10)));
        Assert.Equal("dentro", (await ConvertAsync(nested, MediaTypes.Docx)).Markdown);
    }

    [Fact]
    public async Task OfficePartsLargerThanTheCharacterLimitAreRejected()
    {
        var docx = Docx(string.Concat(Enumerable.Repeat("<w:p><w:r><w:t>relleno relleno relleno</w:t></w:r></w:p>", 200)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ConvertAsync(docx, MediaTypes.Docx, new MarkdownConverterOptions { MaxOfficePartChars = 2000 }));

        Assert.Contains("MaxOfficePartChars", error.Message, StringComparison.Ordinal);
        Assert.Contains("relleno", (await ConvertAsync(docx, MediaTypes.Docx)).Markdown);
    }

    [Fact]
    public async Task CellsBeyondExcelsLastColumnAreIgnored()
    {
        // ZZZZZ1 desbordaba a 111 millones de columnas y ZZZZZZ1 agotaba la memoria.
        var xlsx = Xlsx(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>inicio</t></is></c><c r=\"ZZZZZZ1\" t=\"inlineStr\"><is><t>fuera</t></is></c><c r=\"ZZZZZ1\"><v>1</v></c><c r=\"XFE1\"><v>2</v></c></row>"
            + "<row r=\"2\"><c r=\"XFD2\" t=\"inlineStr\"><is><t>ultima</t></is></c><c r=\"B2\"><v>3</v></c></row>");

        var result = await ConvertAsync(xlsx, MediaTypes.Xlsx);

        Assert.DoesNotContain("fuera", result.Markdown);
        Assert.Contains("| inicio |  |  |", result.Markdown);
        Assert.Contains("|  | 3 | ultima |", result.Markdown); // las columnas vacías intermedias no se escriben
        Assert.True(result.Markdown.Length < 200, result.Markdown);
    }

    [Fact]
    public async Task RowLimitIsAppliedWhileReadingTheSheet()
    {
        var rows = new StringBuilder();
        for (var i = 1; i <= 5000; i++)
        {
            // Sin atributo r en las celdas: se usan las posiciones.
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{i}\"><c><v>{i}</v></c><c t=\"s\"><v>{i % 2}</v></c></row>");
        }

        var xlsx = Xlsx(rows.ToString(), "<si><t>par</t></si><si><r><t>im</t></r><r><t>par</t></r><rPh><t>FONETICA</t></rPh></si>");

        var result = await ConvertAsync(xlsx, MediaTypes.Xlsx, new MarkdownConverterOptions { MaxRowsPerSheet = 10 });

        Assert.Equal(12, result.Markdown.Split('\n').Count(l => l.StartsWith('|'))); // encabezado, separador y 10 filas de datos
        Assert.Contains("| 2 | par |", result.Markdown);
        Assert.Contains("| 3 | impar |", result.Markdown);
        Assert.DoesNotContain("FONETICA", result.Markdown);
        Assert.Contains("4989 filas más omitidas (total 4999 filas de datos)", result.Markdown);
        Assert.Contains(result.Warnings, w => w.Contains("4989", StringComparison.Ordinal));
    }
}

public class PdfLimitTests
{
    /// <summary>PDF de páginas en blanco (sin texto: todas "necesitan" OCR).</summary>
    private static byte[] BlankPdf(int pages, double width = 595, double height = 842)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(width, height);
        }

        return builder.Build();
    }

    /// <summary>Rasterizador falso que registra páginas y resolución, y devuelve un PNG mínimo.</summary>
    private sealed class CountingRasterizer : IPdfRasterizer
    {
        public System.Collections.Concurrent.ConcurrentQueue<(int Page, int Dpi)> Calls { get; } = new();

        public Task<IReadOnlyDictionary<int, byte[]>> RenderAsync(byte[] pdf, IReadOnlyList<int> pages, int dpi = 200, CancellationToken cancellationToken = default)
        {
            foreach (var page in pages)
            {
                Calls.Enqueue((page, dpi));
            }

            return Task.FromResult<IReadOnlyDictionary<int, byte[]>>(pages.ToDictionary(p => p, _ => File.ReadAllBytes(DocumentConverterTests.Fixture("receipt.png"))));
        }
    }

    private sealed class ProgressLog : IProgress<DocumentConversionProgress>
    {
        public System.Collections.Concurrent.ConcurrentQueue<DocumentConversionProgress> Reports { get; } = new();

        public void Report(DocumentConversionProgress value) => Reports.Enqueue(value);
    }

    [Fact]
    public async Task ThousandsOfBlankPagesDoNotTriggerThousandsOfOcrCalls()
    {
        // Pocos KB y 3000 páginas en blanco: antes, 3000 procesos de OCR o llamadas pagadas de visión.
        var pdf = BlankPdf(3000);
        var ocr = new DocumentConverterTests.FakeOcr("reconocido");
        var rasterizer = new CountingRasterizer();
        var progress = new ProgressLog();
        var watch = Stopwatch.StartNew();

        var result = await new MarkdownConverter(new MarkdownConverterOptions { Ocr = ocr, PdfRasterizer = rasterizer, Progress = progress })
            .ConvertAsync(pdf, MediaTypes.Pdf, "blancas.pdf", TestContext.Current.CancellationToken);

        Assert.True(pdf.Length < 1_000_000);
        Assert.Equal(1000, result.Pages.Count); // MaxPdfPages
        Assert.Equal(100, ocr.Calls); // MaxOcrPages
        Assert.Equal(100, rasterizer.Calls.Count);
        Assert.Equal(100, result.Pages.Count(p => p.Source == PageTextSource.Ocr));
        Assert.Contains(result.Warnings, w => w.Contains("MaxPdfPages", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("900 página(s) o imagen(es)", StringComparison.Ordinal) && w.Contains("MaxOcrPages = 100", StringComparison.Ordinal));
        Assert.Equal(100, progress.Reports.Max(r => r.OcrPages));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Tardó {watch.Elapsed}.");
    }

    [Fact]
    public async Task WholeDocumentOcrIsOnlyUsedWhenAllPagesFitTheBudget()
    {
        var ocr = new DocumentConverterTests.FakeOcr("texto", MediaTypes.Pdf, "image/png");
        var options = new MarkdownConverterOptions { Ocr = ocr, PdfRasterizer = new CountingRasterizer(), MaxOcrPages = 5 };

        var small = await new MarkdownConverter(options).ConvertAsync(BlankPdf(5), MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([MediaTypes.Pdf], ocr.MediaTypesSeen); // una sola llamada con el PDF completo
        Assert.Equal("texto", small.Pages[0].Markdown);

        // 50 páginas: el motor cobraría las 50; se reconocen página por página solo las 5 permitidas.
        ocr.MediaTypesSeen.Clear();
        var large = await new MarkdownConverter(options).ConvertAsync(BlankPdf(50), MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(MediaTypes.Pdf, ocr.MediaTypesSeen);
        Assert.Equal(5, ocr.MediaTypesSeen.Count);
        Assert.Equal(5, large.Pages.Count(p => p.Source == PageTextSource.Ocr));
    }

    [Fact]
    public async Task HugePagesAreRasterizedAtALowerResolution()
    {
        var rasterizer = new CountingRasterizer();
        var converter = new MarkdownConverter(new MarkdownConverterOptions { Ocr = new DocumentConverterTests.FakeOcr("x"), PdfRasterizer = rasterizer });

        // 200 pulgadas por lado: a 200 ppp serían 40.000 × 40.000 píxeles (1.600 millones).
        await converter.ConvertAsync(BlankPdf(1, 14_400, 14_400), MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);
        await converter.ConvertAsync(BlankPdf(1), MediaTypes.Pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([25, 200], rasterizer.Calls.Select(c => c.Dpi));
    }

    [Fact]
    public async Task PdftoppmOutputIsCappedInPixels()
    {
        var pdftoppm = PdftoppmRasterizer.TryCreate();
        Assert.SkipWhen(pdftoppm is null, "pdftoppm no instalado");
        var rasterizer = new PdftoppmRasterizer { MaxPixels = 300 };

        var images = await rasterizer.RenderAsync(BlankPdf(1, 2000, 1000), [1], dpi: 72, cancellationToken: TestContext.Current.CancellationToken);

        var png = images[1];
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Assert.Equal((300, 300), (width, height));
    }
}

public class ExternalProcessSecurityTests
{
    [Fact]
    public async Task ToolsDoNotInheritSecretsUnlessExplicitlyAllowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "usa sh");
        Environment.SetEnvironmentVariable("AISDK_TEST_API_KEY", "sk-secreto");

        var filtered = await new ExternalProcessRunner().RunCheckedAsync("sh", ["-c", "echo ${AISDK_TEST_API_KEY:-vacio}"], cancellationToken: TestContext.Current.CancellationToken);
        var inherited = await new ExternalProcessRunner(new ExternalProcessOptions { AllowedEnvironmentVariables = null })
            .RunCheckedAsync("sh", ["-c", "echo ${AISDK_TEST_API_KEY:-vacio}"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("vacio", Encoding.UTF8.GetString(filtered).Trim());
        Assert.Equal("sk-secreto", Encoding.UTF8.GetString(inherited).Trim());
        Assert.Same(ExternalProcessOptions.CommonEnvironmentVariables, new ExternalProcessOptions().AllowedEnvironmentVariables);
    }

    [Fact]
    public async Task TimeoutKillsTheWholeProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "usa sh");
        var marker = Path.Combine(Path.GetTempPath(), "aisdk-hijo-" + Guid.NewGuid().ToString("N"));
        var runner = new ExternalProcessRunner(new ExternalProcessOptions { Timeout = TimeSpan.FromMilliseconds(300) });
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("sh", ["-c", $"(sleep 1; touch '{marker}') & sleep 30"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("tiempo máximo", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Tardó {watch.Elapsed}.");
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker)); // el hijo también se detuvo
    }

    [Fact]
    public async Task OutputIsCappedAndErrorIsTruncated()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "usa sh");
        var runner = new ExternalProcessRunner(new ExternalProcessOptions { MaxOutputBytes = 100_000 });

        var overflow = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("sh", ["-c", "yes"], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("máximo de 100000 bytes", overflow.Message, StringComparison.Ordinal);

        var noisy = await runner.RunAsync("sh", ["-c", "head -c 2000000 /dev/zero | tr '\\0' e >&2; echo ok"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ok", Encoding.UTF8.GetString(noisy.Output).Trim());
        Assert.InRange(noisy.Error.Length, ExternalProcessRunner.MaxErrorBytes, ExternalProcessRunner.MaxErrorBytes + 1);
    }

    /// <summary>Ejecutor que registra los argumentos y delega en el real (o comprueba el directorio temporal).</summary>
    private sealed class RecordingRunner(IExternalProcessRunner inner) : IExternalProcessRunner
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public bool Exists(string executable) => inner.Exists(executable);

        public Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((executable, arguments));
            return inner.RunAsync(executable, arguments, input, cancellationToken);
        }
    }

    [Fact]
    public async Task FfmpegCannotFollowPlaylistsToTheNetwork()
    {
        Assert.SkipUnless(ExternalProcess.Exists("ffmpeg"), "ffmpeg no instalado");
        await using var server = new LocalHttpServer(_ => Task.FromResult(LocalHttpServer.Response(404, string.Empty)));

        // Un "video" que en realidad es un manifiesto DASH: sin listas blancas, ffmpeg pedía el segmento por HTTP.
        var manifest = $"""
            <?xml version="1.0"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S" minBufferTime="PT1S" profiles="urn:mpeg:dash:profile:isoff-on-demand:2011">
              <Period><AdaptationSet mimeType="video/mp4"><Representation id="1" bandwidth="1000"><BaseURL>{server.Url("/secreto.mp4")}</BaseURL></Representation></AdaptationSet></Period>
            </MPD>
            """;
        var runner = new RecordingRunner(ExternalProcess.Runner);
        var converter = new MarkdownConverter(new MarkdownConverterOptions { Transcription = new SilentTranscription(), ProcessRunner = runner });

        var result = await converter.ConvertAsync(Encoding.UTF8.GetBytes(manifest), "video/mp4", "video.mp4", TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("audio", StringComparison.Ordinal));
        Assert.Empty(server.Paths);
        Assert.All(runner.Calls, c =>
        {
            Assert.Contains("-protocol_whitelist", c.Arguments);
            Assert.Contains("-format_whitelist", c.Arguments);
        });
        Assert.Contains(runner.Calls, c => c.Arguments.Contains("-nostdin"));
    }

    [Fact]
    public async Task OnlyTheFirstMaxMediaDurationIsAnalyzed()
    {
        Assert.SkipUnless(ExternalProcess.Exists("ffmpeg") && ExternalProcess.Exists("ffprobe"), "ffmpeg o ffprobe no instalados");
        var runner = new RecordingRunner(ExternalProcess.Runner);
        var converter = new MarkdownConverter(new MarkdownConverterOptions
        {
            Ocr = new DocumentConverterTests.FakeOcr("escena"),
            ProcessRunner = runner,
            MaxMediaDuration = TimeSpan.FromSeconds(3),
            VideoFrameInterval = TimeSpan.FromSeconds(1),
        });

        var result = await converter.ConvertFileAsync(DocumentConverterTests.Fixture("clip.mp4"), TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("MaxMediaDuration", StringComparison.Ordinal));
        var ffmpeg = runner.Calls.Single(c => c.Arguments.Contains("-vf"));
        var limit = ffmpeg.Arguments.ToList().IndexOf("-t");
        Assert.Equal("3", ffmpeg.Arguments[limit + 1]);
        Assert.True(limit < ffmpeg.Arguments.ToList().IndexOf("-i"));
    }

    [Fact]
    public async Task TemporaryDirectoriesArePrivate()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "permisos Unix");
        UnixFileMode? mode = null;
        var runner = new InspectingRunner(arguments =>
        {
            var input = arguments.First(a => a.EndsWith("in.pdf", StringComparison.Ordinal));
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(Path.GetDirectoryName(input)!);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PdftoppmRasterizer("pdftoppm", runner).RenderAsync([1, 2, 3], [1], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    private sealed class InspectingRunner(Action<IReadOnlyList<string>> inspect) : IExternalProcessRunner
    {
        public bool Exists(string executable) => true;

        public Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
        {
            inspect(arguments);
            return Task.FromResult(new ExternalProcess.Result(1, [], "falso"));
        }
    }

    private sealed class SilentTranscription : ITranscriptionModel
    {
        public string Provider => "fake";

        public string ModelId => "fake";

        public Task<TranscriptionModelResult> TranscribeAsync(TranscriptionModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranscriptionModelResult(string.Empty));
    }
}
