using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiSdk.Anthropic;
using AiSdk.Bedrock;
using AiSdk.Extensions.DependencyInjection;
using AiSdk.Http;
using AiSdk.OpenAI;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.Tests;

/// <summary>Las pruebas que cambian variables de entorno del proceso no se ejecutan en paralelo con otras.</summary>
[CollectionDefinition(nameof(ProcessEnvironmentTests), DisableParallelization = true)]
public sealed class ProcessEnvironmentTests;

/// <summary>Regresiones de la capa HTTP y de los proveedores.</summary>
[Collection(nameof(ProcessEnvironmentTests))]
public sealed class HttpHardeningTests
{
    private const string ChatCompletion = """{"id":"c1","model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"hola"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    // -----------------------------------------------------------------
    // #10 La clave de OpenAI no va a servidores compatibles de terceros
    // -----------------------------------------------------------------

    [Fact]
    public async Task OpenAIKeyIsNeverSentToCompatibleServers()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-openai-secreta");
        Environment.SetEnvironmentVariable("AISDK_TEST_GROQ_KEY", "gsk-groq");
        try
        {
            // Compatible() de extremo a extremo, contra un servidor local (y el cliente HTTP compartido).
            using var server = new LoopbackServer(_ => (200, ChatCompletion, null));
            var ollama = OpenAIProvider.Compatible("ollama", server.Url + "v1");
            Assert.Equal("hola", (await Ai.GenerateTextAsync(new GenerateTextOptions { Model = ollama.Chat("llama3.2"), Prompt = "x", MaxRetries = 0 })).Text);
            Assert.Null(server.Requests.Single().Headers["Authorization"]);

            // Opciones equivalentes con BaseUrl propia: sin la variable de OpenAI, aunque se exija clave.
            var handler = new FakeHandler().Json(ChatCompletion).Json(ChatCompletion).Json(ChatCompletion);
            var custom = new OpenAIProvider(new OpenAIProviderOptions { Name = "groq", BaseUrl = "https://api.groq.com/openai/v1", HttpClient = new HttpClient(handler) });
            var missing = await Assert.ThrowsAsync<AiSdkException>(() => custom.Chat("llama").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }, TestContext.Current.CancellationToken));
            Assert.Contains("ApiKeyEnvironmentVariable", missing.Message);
            Assert.Empty(handler.Requests);

            // Una "BaseUrl" que solo empieza como la de OpenAI tampoco recibe la clave.
            var lookalike = new OpenAIProvider(new OpenAIProviderOptions { BaseUrl = "https://api.openai.com.evil.example/v1", RequireApiKey = false, HttpClient = new HttpClient(handler) });
            await lookalike.Chat("gpt").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }, TestContext.Current.CancellationToken);
            Assert.False(handler.Requests[^1].Request.Headers.Contains("Authorization"));

            // La variable del propio servidor, indicada explícitamente, sí se usa.
            var groq = new OpenAIProvider(new OpenAIProviderOptions { BaseUrl = "https://api.groq.com/openai/v1", ApiKeyEnvironmentVariable = "AISDK_TEST_GROQ_KEY", HttpClient = new HttpClient(handler) });
            await groq.Chat("llama").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }, TestContext.Current.CancellationToken);
            Assert.Equal("Bearer gsk-groq", handler.Requests[^1].Request.Headers.Authorization!.ToString());

            // Con el endpoint oficial, OPENAI_API_KEY se sigue leyendo como siempre.
            var official = new OpenAIProvider(new OpenAIProviderOptions { HttpClient = new HttpClient(handler) });
            await official.Chat("gpt-5").GenerateAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }, TestContext.Current.CancellationToken);
            Assert.Equal("Bearer sk-openai-secreta", handler.Requests[^1].Request.Headers.Authorization!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
            Environment.SetEnvironmentVariable("AISDK_TEST_GROQ_KEY", null);
        }
    }

    // -----------------------------------------------------------------
    // #11 SSE: límites, inactividad y eventos mal formados
    // -----------------------------------------------------------------

    [Fact]
    public async Task OpenAICompatibleStreamsTolerateKeepAlivesAndATruncatedLastEvent()
    {
        var body =
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hola\"}}]}\n\n" +
            ": comentario\n\n" +
            "data: keep-alive\n\n" +
            "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" mundo\"},\"finish_reason\":\"stop\"}]}\r\n\r\n" +
            "data: {\"id\":\"c1\",\"usage\":{\"prompt_"; // la conexión se cortó a mitad del último evento
        var openai = new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k", HttpClient = new HttpClient(new RawHandler(body)) });

        var result = Ai.StreamText(new GenerateTextOptions { Model = openai.Chat("m"), Prompt = "x" });

        Assert.Equal("Hola mundo", await result.Text);
        Assert.Equal(FinishReason.Stop, await result.FinishReason);
    }

    [Fact]
    public async Task AMalformedCompleteEventFailsWithAClearError()
    {
        var body = "data: {\"id\":\"c1\",\"choices\":[\n\n" + "data: {\"id\":\"c1\",\"choices\":[]}\n\n";
        var openai = new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k", HttpClient = new HttpClient(new RawHandler(body)) });

        var error = await Assert.ThrowsAsync<AiSdkException>(async () =>
        {
            await foreach (var _ in openai.Chat("m").StreamAsync(new LanguageModelRequest { Messages = [ModelMessage.User("x")] }, TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Contains("JSON inválido", error.Message);
    }

    [Fact]
    public async Task SseLinesAndEventsAreSizeCapped()
    {
        async Task<List<SseEvent>> ReadAsync(string body, SseReadOptions options)
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            var events = new List<SseEvent>();
            await foreach (var e in ProviderHttp.ReadSseAsync(response, options, TestContext.Current.CancellationToken))
            {
                events.Add(e);
            }

            return events;
        }

        var limits = new SseReadOptions { MaxLineChars = 100, MaxEventChars = 150 };
        await Assert.ThrowsAsync<AiSdkException>(() => ReadAsync("data: " + new string('x', 1000) + "\n\n", limits));
        await Assert.ThrowsAsync<AiSdkException>(() => ReadAsync(string.Concat(Enumerable.Repeat("data: " + new string('x', 60) + "\n", 5)) + "\n", limits));

        // Saltos de línea CR, LF y CRLF (como exige la especificación) y comentarios.
        var events = await ReadAsync("data: a\r\r: ping\nevent: e\ndata: b\r\ndata: c\r\n\r\n", limits);
        Assert.Equal(["a", "b\nc"], events.Select(e => e.Data));
        Assert.Equal("e", events[1].Event);
        Assert.All(events, e => Assert.False(e.Incomplete));
    }

    [Fact]
    public async Task StalledStreamsTimeOut()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream("data: {\"a\":1}\n\n")) };
        var events = new List<SseEvent>();
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var e in ProviderHttp.ReadSseAsync(response, new SseReadOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) }, TestContext.Current.CancellationToken))
            {
                events.Add(e);
            }
        });

        Assert.Single(events);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.NotNull(ProviderHttp.ModelStreamOptions.IdleTimeout); // los modelos tienen un límite por defecto
    }

    // -----------------------------------------------------------------
    // #12 Retry-After
    // -----------------------------------------------------------------

    [Fact]
    public async Task RetryAfterInThePastRetriesImmediately()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var handler = new FakeHandler()
            .Json("""{"error":{"message":"lento"}}""", HttpStatusCode.TooManyRequests, new Dictionary<string, string> { ["Retry-After"] = past })
            .Json(ChatCompletion);
        var openai = new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        // Antes: retraso negativo → Task.Delay lanzaba ArgumentOutOfRangeException en vez de reintentar.
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = openai.Chat("m"), Prompt = "x", CancellationToken = TestContext.Current.CancellationToken });

        Assert.Equal("hola", result.Text);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LongRetryAfterIsNotShortenedButSurfaced()
    {
        var handler = new FakeHandler()
            .Json("""{"error":{"message":"cuota"}}""", HttpStatusCode.TooManyRequests, new Dictionary<string, string> { ["Retry-After"] = "120" })
            .Json(ChatCompletion);
        var openai = new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        // Antes: se ignoraba un Retry-After ≥ 60 s y se reintentaba a los ~2 s.
        var error = await Assert.ThrowsAsync<ApiCallException>(() => Ai.GenerateTextAsync(new GenerateTextOptions { Model = openai.Chat("m"), Prompt = "x" }));
        Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
        Assert.Single(handler.Requests);

        // Dentro del máximo configurado se respeta exactamente.
        var patient = new FakeHandler()
            .Json("""{"error":{"message":"lento"}}""", HttpStatusCode.TooManyRequests, new Dictionary<string, string> { ["Retry-After"] = "1" })
            .Json(ChatCompletion);
        var watch = Stopwatch.StartNew();
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k", HttpClient = new HttpClient(patient) }).Chat("m"),
            Prompt = "x",
            MaxRetryAfter = TimeSpan.FromSeconds(5),
        });
        Assert.Equal("hola", result.Text);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    // -----------------------------------------------------------------
    // #13 Redirecciones
    // -----------------------------------------------------------------

    [Fact]
    public async Task RedirectsNeverForwardApiKeysToAnotherOrigin()
    {
        var inner = new RedirectingHandler();
        using var client = new HttpClient(new SafeRedirectHandler(inner));

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("x-api-key", "sk-ant-secreta");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", "goog-secreta");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer secreta");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        // Otro origen: llega sin ninguna cabecera de autenticación.
        using (var cross = await SendAsync(HttpMethod.Get, "https://api.example/files/1?go=https://cdn.example/f"))
        {
            Assert.Equal(HttpStatusCode.OK, cross.StatusCode);
            var final = inner.Requests[^1];
            Assert.Equal("cdn.example", final.RequestUri!.Host);
            Assert.False(final.Headers.Contains("x-api-key"));
            Assert.False(final.Headers.Contains("x-goog-api-key"));
            Assert.False(final.Headers.Contains("Authorization"));
            Assert.True(final.Headers.Contains("Accept"));
        }

        // Mismo origen: se conservan.
        using (await SendAsync(HttpMethod.Get, "https://api.example/files/1?go=https://api.example/files/2"))
        {
            Assert.True(inner.Requests[^1].Headers.Contains("x-api-key"));
        }

        // Un POST no se redirige y de HTTPS a HTTP tampoco.
        using (var post = await SendAsync(HttpMethod.Post, "https://api.example/v1/messages?go=https://evil.example/v1/messages"))
        {
            Assert.Equal(HttpStatusCode.TemporaryRedirect, post.StatusCode);
        }

        using (var downgrade = await SendAsync(HttpMethod.Get, "https://api.example/f?go=http://api.example/f"))
        {
            Assert.Equal(HttpStatusCode.TemporaryRedirect, downgrade.StatusCode);
        }

        Assert.DoesNotContain(inner.Requests, r => r.RequestUri!.Host == "evil.example" || r.RequestUri.Scheme == "http");
    }

    [Fact]
    public async Task SharedAndDependencyInjectionClientsDoNotLeakKeysOnRedirect()
    {
        using var target = new LoopbackServer(_ => (200, "{}", null));
        using var origin = new LoopbackServer(_ => (307, string.Empty, target.Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "destino"));

        var services = new ServiceCollection();
        services.AddAiSdk(ai => ai.AddAnthropic(o => o.ApiKey = "k"));
        await using var provider = services.BuildServiceProvider();
        var injected = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AiSdk.anthropic");

        foreach (var client in new[] { ProviderHttp.DefaultClient, injected })
        {
            using var request = ProviderHttp.CreateRequest(HttpMethod.Get, new Uri(origin.Url + "archivo"), [new("x-api-key", "sk-ant-secreta")]);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, target.Requests.Count);
        Assert.All(target.Requests, r => Assert.Null(r.Headers["x-api-key"]));
    }

    // -----------------------------------------------------------------
    // #14 Ids escapados en las rutas
    // -----------------------------------------------------------------

    [Fact]
    public async Task ResourceIdsAreEscapedInUrlPaths()
    {
        const string hostile = "../../organization/admin_api_keys?x=1#";
        var handler = new FakeHandler().Bytes([1], "application/octet-stream")
            .Json("""{"id":"vs","name":"n","status":"completed"}""")
            .Json("""{"id":"b","status":"completed"}""")
            .Json("""{"id":"b","processing_status":"ended"}""");
        var openai = new OpenAIProvider(new OpenAIProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });
        var anthropic = new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        await openai.Files.DownloadAsync(hostile, TestContext.Current.CancellationToken);
        await openai.VectorStores.GetAsync(hostile, TestContext.Current.CancellationToken);
        await openai.Batches("gpt-5").GetAsync(hostile, TestContext.Current.CancellationToken);
        await anthropic.Batches("claude").GetAsync(hostile, TestContext.Current.CancellationToken);

        var escaped = Uri.EscapeDataString(hostile);
        Assert.Equal(
            [$"/v1/files/{escaped}/content", $"/v1/vector_stores/{escaped}", $"/v1/batches/{escaped}", $"/v1/messages/batches/{escaped}"],
            handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath));
        Assert.All(handler.Requests, r => Assert.Empty(r.Request.RequestUri!.Query));
    }

    [Fact]
    public async Task GoogleModelIdsAreEscapedAndCannotTraverseTheApi()
    {
        var handler = new FakeHandler().Json("""{"candidates":[{"content":{"role":"model","parts":[{"text":"hola"}]},"finishReason":"STOP"}]}""");
        var google = new AiSdk.Google.GoogleProvider(new AiSdk.Google.GoogleProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = google.Gemini("gemini x?key=1#"), Prompt = "hola" });
        Assert.Equal("/v1beta/models/gemini%20x%3Fkey%3D1%23:generateContent", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Empty(handler.Requests[0].Request.RequestUri!.Query);

        await Assert.ThrowsAsync<ArgumentException>(() => Ai.GenerateTextAsync(new GenerateTextOptions { Model = google.Gemini("../../files"), Prompt = "hola" }));
    }

    // -----------------------------------------------------------------
    // #15 Bedrock: tamaño declarado por el servidor
    // -----------------------------------------------------------------

    [Fact]
    public async Task BedrockEventStreamRejectsHugeDeclaredMessages()
    {
        var prelude = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(prelude.AsSpan(0, 4), int.MaxValue - 8);
        BinaryPrimitives.WriteInt32BigEndian(prelude.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(prelude.AsSpan(8, 4), AwsEventStream.Crc32(prelude.AsSpan(0, 8)));

        // Antes: reservaba ~2 GB a partir de los 12 bytes del preludio.
        var error = await Assert.ThrowsAsync<AiSdkException>(async () =>
        {
            await foreach (var _ in AwsEventStream.ReadAsync(new MemoryStream(prelude), TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Contains("máximo", error.Message);

        // Los mensajes normales se siguen leyendo.
        var message = AwsEventStream.Encode(new Dictionary<string, string> { [":event-type"] = "messageStop" }, "{}"u8.ToArray());
        var read = new List<AwsEventStreamMessage>();
        await foreach (var m in AwsEventStream.ReadAsync(new MemoryStream(message), TestContext.Current.CancellationToken))
        {
            read.Add(m);
        }

        Assert.Equal("messageStop", Assert.Single(read).EventType);
    }

    // -----------------------------------------------------------------
    // Utilidades
    // -----------------------------------------------------------------

    /// <summary>Responde con un cuerpo SSE tal cual (sin agregar líneas en blanco).</summary>
    private sealed class RawHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    /// <summary>Redirige (307) a la URL del parámetro <c>go</c>; registra cada petición recibida.</summary>
    private sealed class RedirectingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var query = request.RequestUri!.Query;
            var go = query.StartsWith("?go=", StringComparison.Ordinal) ? query[4..] : null;
            var response = go is null
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri(go) } };
            return Task.FromResult(response);
        }
    }

    /// <summary>Envía un texto y luego no envía nada más hasta que se cancele la lectura.</summary>
    private sealed class StallingStream(string first) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Servidor HTTP local mínimo (127.0.0.1 y localhost) que registra las cabeceras recibidas.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerRequest, (int Status, string Body, string? Location)> _respond;

        public LoopbackServer(Func<HttpListenerRequest, (int Status, string Body, string? Location)> respond)
        {
            _respond = respond;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public List<(string Path, System.Collections.Specialized.NameValueCollection Headers)> Requests { get; } = [];

        public void Dispose() => _listener.Close();

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                lock (Requests)
                {
                    Requests.Add((context.Request.Url!.AbsolutePath, new System.Collections.Specialized.NameValueCollection(context.Request.Headers)));
                }

                var (status, body, location) = _respond(context.Request);
                context.Response.StatusCode = status;
                if (location is not null)
                {
                    context.Response.RedirectLocation = location;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }
}
