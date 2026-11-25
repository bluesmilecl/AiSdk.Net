using System.Text;
using System.Text.Json.Nodes;
using AiSdk.UI;

namespace AiSdk.Tests;

public class StreamExtrasTests
{
    [Fact]
    public async Task SmoothTransformChunksByWord()
    {
        var model = new MockLanguageModel { StreamChunkSize = 100 }.ReturnsText("Hola mundo desde punto net");
        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            StreamTransform = StreamTransforms.Smooth(TimeSpan.Zero),
        });

        var chunks = new List<string>();
        await foreach (var c in stream.TextStream)
        {
            chunks.Add(c);
        }

        Assert.Equal(["Hola ", "mundo ", "desde ", "punto ", "net"], chunks);
    }

    [Fact]
    public async Task ExtractReasoningFromThinkTags()
    {
        var inner = new MockLanguageModel().ReturnsText("<think>Primero sumo</think>\nEl resultado es 4");
        var model = inner.WithMiddleware(Middlewares.ExtractReasoning());

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "2+2" });

        Assert.Equal("Primero sumo", result.ReasoningText);
        Assert.Equal("El resultado es 4", result.Text);
    }

    [Fact]
    public async Task ExtractReasoningWhileStreamingSplitTags()
    {
        // Fragmentos de 3 caracteres: las etiquetas llegan partidas entre fragmentos.
        var inner = new MockLanguageModel { StreamChunkSize = 3 }.ReturnsText("<think>Pienso un poco</think>\nRespuesta final");
        var model = inner.WithMiddleware(Middlewares.ExtractReasoning());

        var result = await Ai.StreamText(new GenerateTextOptions { Model = model, Prompt = "x" }).Result;

        Assert.Equal("Pienso un poco", result.ReasoningText);
        Assert.Equal("Respuesta final", result.Text);
    }

    [Fact]
    public async Task SimulateStreamingUsesGenerate()
    {
        var generateOnly = new GenerateOnlyModel();
        var model = generateOnly.WithMiddleware(Middlewares.SimulateStreaming());

        Assert.Equal("completo", await Ai.StreamText(model, "x", TestContext.Current.CancellationToken).Text);
    }

    [Fact]
    public async Task ToolsCanWriteDataPartsToTheUI()
    {
        var tool = Tool.Create<WeatherInput, string>("search", "busca", (input, ctx, _) =>
        {
            ctx.WriteData("progress", new { percent = 50 }, id: "p1");
            ctx.WriteData("progress", new { percent = 100 }, id: "p1", transient: true);
            return Task.FromResult("ok");
        });
        var model = new MockLanguageModel().ReturnsToolCall("search", new { city = "x" }).ReturnsText("listo");
        var stream = Ai.StreamText(new GenerateTextOptions { Model = model, Prompt = "x", Tools = [tool], StopWhen = StopCondition.StepCountIs(3) });

        using var output = new MemoryStream();
        await UIMessageStream.WriteAsync(stream.FullStream, output, cancellationToken: TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("""{"type":"data-progress","id":"p1","data":{"percent":50}}""", text);
        Assert.Contains("\"transient\":true", text);
    }

    [Fact]
    public async Task CustomUIStreamMergesTextAndData()
    {
        var model = new MockLanguageModel().ReturnsText("hola");
        var chunks = new List<JsonObject>();
        await foreach (var chunk in UIMessageStream.Create(writer =>
        {
            writer.WriteData("status", new { step = "buscando" });
            writer.WriteSource("https://example.com/a");
            writer.WriteSource("https://example.com/b", "Docs");
            writer.Merge(Ai.StreamText(model, "x"));
            return Task.CompletedTask;
        }, messageId: "m1", cancellationToken: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var types = chunks.Select(c => (string?)c["type"]).ToList();
        var sources = chunks.Where(c => (string?)c["type"] == "source-url").ToList();
        Assert.False(sources[0].ContainsKey("title")); // ai 6.0.x rechaza "title": null
        Assert.Equal("Docs", (string?)sources[1]["title"]);
        Assert.Equal("start", types[0]);
        Assert.Equal("finish", types[^1]);
        Assert.Single(types, t => t == "start");
        Assert.Single(types, t => t == "finish");
        Assert.Contains("data-status", types);
        Assert.Contains("text-delta", types);
    }

    [Fact]
    public async Task ResumableStreamCanBeReadAgainAfterDisconnect()
    {
        var store = new InMemoryResumableStreamStore();
        var model = new MockLanguageModel { StreamChunkSize = 2 }.ReturnsText("abcdef");
        var streamId = await ResumableUIMessageStream.StartAsync(store, "chat-1", UIMessageStream.ToChunks(Ai.StreamText(model, "x", TestContext.Current.CancellationToken).FullStream));

        var first = new List<string>();
        await foreach (var e in store.ReadAsync(streamId, TestContext.Current.CancellationToken))
        {
            first.Add(e);
        }

        var replay = new List<string>();
        await foreach (var e in store.ReadAsync(streamId, TestContext.Current.CancellationToken))
        {
            replay.Add(e);
        }

        Assert.Equal(first, replay);
        Assert.Equal("data: [DONE]\n\n", first[^1]);
        Assert.Equal(3, first.Count(e => e.Contains("text-delta")));
        // Al terminar, el chat ya no tiene stream activo (el endpoint de reanudación responde 204).
        Assert.Null(await store.GetActiveStreamAsync("chat-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResumeEndpointReturnsNoContentWithoutActiveStream()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        await AiSdk.AspNetCore.UIMessageStreamResults.Resume(new InMemoryResumableStreamStore(), "nada").ExecuteAsync(context);
        Assert.Equal(204, context.Response.StatusCode);
    }

    private sealed class GenerateOnlyModel : ILanguageModel
    {
        public string Provider => "x";

        public string ModelId => "x";

        public Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LanguageModelResult { Content = [new TextPart("completo")], FinishReason = FinishReason.Stop });

        public IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

public class RetrievalTests
{
    private sealed class KeywordEmbeddingModel : IEmbeddingModel
    {
        private static readonly string[] Vocabulary = ["gato", "perro", "clima", "lluvia", "dotnet", "csharp"];

        public string Provider => "test";

        public string ModelId => "kw";

        public int? MaxEmbeddingsPerCall => null;

        public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingModelResult(
                values.Select(v => Vocabulary.Select(w => v.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1f : 0.01f).ToArray()).ToList(),
                null));
    }

    [Fact]
    public async Task EmbeddingRerankerOrdersByRelevance()
    {
        var reranker = new EmbeddingRerankingModel(new KeywordEmbeddingModel());
        var result = await Ai.RerankAsync(reranker, "lluvia y clima", ["el gato duerme", "mañana habrá lluvia, mal clima", "dotnet y csharp"], topN: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("mañana habrá lluvia, mal clima", result.RerankedDocuments[0]);
        Assert.Equal(1, result.Ranking[0].OriginalIndex);
        Assert.Equal(2, result.Ranking.Count);
    }

    [Fact]
    public async Task InMemoryVectorStoreSearchesAndWorksAsTool()
    {
        var store = new InMemoryVectorStore(new KeywordEmbeddingModel());
        await store.UpsertAsync([
            new VectorDocument("1", "El perro ladra"),
            new VectorDocument("2", "Pronóstico: lluvia y clima frío") { Metadata = new Dictionary<string, string> { ["tipo"] = "clima" } },
            new VectorDocument("3", "Novedades de dotnet"),
        ], TestContext.Current.CancellationToken);

        var hits = await store.SearchAsync("¿habrá lluvia?", topK: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("2", hits[0].Document.Id);

        var filtered = await store.SearchAsync("lluvia", topK: 3, filter: VectorFilter.Ne("tipo", "clima"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(filtered, h => h.Document.Id == "2");
        var onlyClima = await store.SearchAsync("dotnet", topK: 3, filter: VectorFilter.Eq("tipo", "clima") | VectorFilter.In("tipo", "x", "y"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["2"], onlyClima.Select(h => h.Document.Id));

        var model = new MockLanguageModel()
            .ReturnsToolCall("search_knowledge_base", new { query = "clima con lluvia" })
            .ReturnsText("Lloverá");
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", Tools = [store.AsTool(topK: 1)], StopWhen = StopCondition.StepCountIs(3) });
        Assert.Equal("2", result.Steps[0].ToolResults[0].Output[0].GetProperty("id").GetString());
    }

    [Fact]
    public void TextChunkerRespectsSizeAndOverlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Párrafo número {i} con algo de texto para indexar."));
        var chunks = TextChunker.Split(text, maxChars: 200, overlap: 20);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 220));
        Assert.Contains("Párrafo número 20", chunks[^1]);
    }

    [Fact]
    public async Task CountTokensRequiresSupportingModel()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Ai.CountTokensAsync(new GenerateTextOptions { Model = new MockLanguageModel(), Prompt = "x" }));
    }

    [Fact]
    public async Task BatchItemsAreBuiltFromOptions()
    {
        var item = await new GenerateTextOptions { Model = new MockLanguageModel(), System = "sys", Prompt = "hola", MaxOutputTokens = 50 }.ToBatchItemAsync("req-1");

        Assert.Equal("req-1", item.CustomId);
        Assert.Equal(2, item.Request.Messages.Count);
        Assert.Equal(50, item.Request.MaxOutputTokens);
    }

    [Fact]
    public void FileIdPartsRoundTripThroughJson()
    {
        var message = ModelMessage.User(FilePart.FromFileId("file_123", "application/pdf", "doc.pdf"), ImagePart.FromFileId("file_456"));
        var json = System.Text.Json.JsonSerializer.Serialize(message, JsonSchema.DefaultSerializerOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<ModelMessage>(json, JsonSchema.DefaultSerializerOptions)!;

        Assert.Equal("file_123", Assert.IsType<FilePart>(back.Content[0]).FileId);
        Assert.Equal("file_456", Assert.IsType<ImagePart>(back.Content[1]).FileId);
    }
}

public class PgVectorFilterTests
{
    [Fact]
    public void TranslatesFiltersToParameterizedSql()
    {
        var parameters = new List<object> { "vector", 5 };
        var sql = AiSdk.PgVector.PgVectorFilterTranslator.Translate(
            (VectorFilter.Eq("tipo", "manual") | VectorFilter.In("pais", "CL", "PE")) & !VectorFilter.Exists("borrador"),
            parameters);

        // Contención jsonb (@>): la sirve el índice GIN jsonb_path_ops del almacén.
        Assert.Equal(
            "((metadata @> $3::jsonb OR metadata @> ANY($4::jsonb[])) AND NOT (metadata ? $5))",
            sql);
        Assert.Equal("""{"tipo":"manual"}""", parameters[2]);
        Assert.Equal(["""{"pais":"CL"}""", """{"pais":"PE"}"""], (string[])parameters[3]);
        Assert.Equal("borrador", parameters[4]);
        Assert.DoesNotContain("manual", sql); // los valores nunca se concatenan en el SQL
    }

    [Fact]
    public void CombinesEqualitiesOfAnAndIntoOneContainment()
    {
        var parameters = new List<object>();
        var sql = AiSdk.PgVector.PgVectorFilterTranslator.Translate(
            VectorFilter.Eq("modelo", "m1") & (VectorFilter.Eq("usuario", "u\"7") & VectorFilter.In("modo", "chat"))
            & VectorFilter.Eq("modelo", "m2") & VectorFilter.Ne("conversacion", "c1"),
            parameters);

        Assert.Equal("(metadata @> $1::jsonb AND metadata @> $2::jsonb AND NOT (metadata @> $3::jsonb))", sql);
        Assert.Equal("{\"modelo\":\"m1\",\"usuario\":\"u\\u00227\",\"modo\":\"chat\"}", parameters[0]); // comillas escapadas en el JSON
        Assert.Equal("""{"modelo":"m2"}""", parameters[1]);
        Assert.Equal("""{"conversacion":"c1"}""", parameters[2]);
    }

    [Fact]
    public void TranslatesEmptyAndSingleValueFilters()
    {
        var parameters = new List<object>();
        Assert.Equal("FALSE", AiSdk.PgVector.PgVectorFilterTranslator.Translate(VectorFilter.In("pais"), parameters));
        Assert.Equal("TRUE", AiSdk.PgVector.PgVectorFilterTranslator.Translate(VectorFilter.And(), parameters));
        Assert.Equal("FALSE", AiSdk.PgVector.PgVectorFilterTranslator.Translate(VectorFilter.Or(), parameters));
        Assert.Equal("metadata @> $1::jsonb", AiSdk.PgVector.PgVectorFilterTranslator.Translate(VectorFilter.In("pais", "CL", "CL"), parameters));
        Assert.Equal("""{"pais":"CL"}""", Assert.Single(parameters));
    }

    [Fact]
    public void RejectsUnsafeIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => AiSdk.PgVector.PgVectorStore.Create(
            "Host=localhost", new MockEmbeddingModel(), new AiSdk.PgVector.PgVectorStoreOptions { Dimensions = 3, Table = "docs; DROP TABLE x" }));
    }
}
