using AiSdk.PgVector;

namespace AiSdk.IntegrationTests;

/// <summary>
/// Pruebas contra un PostgreSQL real con pgvector. Define <c>AISDK_TEST_PG</c> con la cadena de
/// conexión (p. ej. <c>Host=localhost;Port=5432;Username=postgres;Database=postgres</c>).
/// </summary>
public class PgVectorTests
{
    /// <summary>Embeddings deterministas por palabras clave (sin API key).</summary>
    private sealed class KeywordEmbeddings : IEmbeddingModel
    {
        public static readonly string[] Vocabulary = ["devolucion", "envio", "horario", "pago", "garantia", "factura"];

        public string Provider => "test";

        public string ModelId => "kw";

        public int? MaxEmbeddingsPerCall => null;

        public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingModelResult(
                values.Select(v => Vocabulary.Select(w => v.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1f : 0.05f).ToArray()).ToList(),
                null));
    }

    private static async Task<PgVectorStore> CreateStoreAsync()
    {
        var connection = Environment.GetEnvironmentVariable("AISDK_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connection), "AISDK_TEST_PG no configurada");
        var store = PgVectorStore.Create(connection!, new KeywordEmbeddings(), new PgVectorStoreOptions
        {
            Dimensions = KeywordEmbeddings.Vocabulary.Length,
            Table = "aisdk_test_" + Guid.NewGuid().ToString("N")[..8],
            TextSearchConfig = "spanish",
        });
        await store.UpsertAsync(
        [
            new VectorDocument("d1", "Política de devolucion: 30 días con boleta.") { Metadata = new Dictionary<string, string> { ["tipo"] = "politica", ["pais"] = "CL" } },
            new VectorDocument("d2", "El envio es gratis sobre $30.000.") { Metadata = new Dictionary<string, string> { ["tipo"] = "politica", ["pais"] = "PE" } },
            new VectorDocument("d3", "Horario de atención: lunes a sábado.") { Metadata = new Dictionary<string, string> { ["tipo"] = "faq" } },
            new VectorDocument("d4", "Código de producto SKU-77812 con garantia de 2 años.") { Metadata = new Dictionary<string, string> { ["tipo"] = "producto" } },
        ]);
        return store;
    }

    [Fact]
    public async Task UpsertSearchFilterAndDelete()
    {
        await using var store = await CreateStoreAsync();
        Assert.Equal(4, await store.CountAsync(TestContext.Current.CancellationToken));

        var hits = await store.SearchAsync("¿cómo hago una devolucion?", topK: 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("d1", hits[0].Document.Id);
        Assert.Equal("CL", hits[0].Document.Metadata!["pais"]);
        Assert.True(hits[0].Score > hits[1].Score);

        var peru = await store.SearchAsync("devolucion", topK: 5, filter: VectorFilter.Eq("pais", "PE"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["d2"], peru.Select(h => h.Document.Id));

        var notPolicies = await store.SearchAsync("devolucion", topK: 5, filter: VectorFilter.Ne("tipo", "politica"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["d3", "d4"], notPolicies.Select(h => h.Document.Id).Order());

        // Upsert actualiza en lugar de duplicar.
        await store.UpsertAsync([new VectorDocument("d3", "Horario nuevo: todos los días.") { Metadata = new Dictionary<string, string> { ["tipo"] = "faq" } }], TestContext.Current.CancellationToken);
        Assert.Equal(4, await store.CountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, await store.DeleteWhereAsync(VectorFilter.Eq("tipo", "politica"), TestContext.Current.CancellationToken));
        await store.DeleteAsync(["d3"], TestContext.Current.CancellationToken);
        Assert.Equal(1, await store.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HybridSearchFindsExactCodes()
    {
        await using var store = await CreateStoreAsync();

        // "SKU-77812" no aporta nada a los embeddings de prueba; la parte de texto completo lo encuentra.
        var hybrid = await store.HybridSearchAsync("SKU-77812", topK: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("d4", hybrid[0].Document.Id);

        var withFilter = await store.HybridSearchAsync("devolucion", topK: 3, filter: VectorFilter.In("pais", "CL", "PE"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(withFilter, h => Assert.Equal("politica", h.Document.Metadata!["tipo"]));
    }

    /// <summary>Embeddings pseudoaleatorios deterministas (32 dimensiones) para tener muchos vectores distintos.</summary>
    private sealed class HashEmbeddings : IEmbeddingModel
    {
        public string Provider => "test";

        public string ModelId => "hash";

        public int? MaxEmbeddingsPerCall => null;

        public static float[] Vector(string value)
        {
            var random = new Random(StringComparer.Ordinal.GetHashCode(value) & 0x7fffffff);
            return Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble()).ToArray();
        }

        public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingModelResult(values.Select(Vector).ToList(), null));
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return 1 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    [Theory]
    [InlineData(PgVectorFilteredSearch.Auto)]
    [InlineData(PgVectorFilteredSearch.Exact)]
    public async Task SelectiveFiltersReturnAllMatchesEvenWhenHnswFiltersThemOut(PgVectorFilteredSearch strategy)
    {
        var connection = Environment.GetEnvironmentVariable("AISDK_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connection), "AISDK_TEST_PG no configurada");
        var ct = TestContext.Current.CancellationToken;

        // Sin seq scan ni bitmap scan el planificador ordena por el índice HNSW (como en una tabla grande): este entrega
        // hnsw.ef_search = 40 candidatos y el filtro de un usuario (1 % de las filas) casi siempre los descarta todos.
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection) { Options = "-c enable_seqscan=off -c enable_bitmapscan=off" };
        var options = new PgVectorStoreOptions { Dimensions = 32, Table = "aisdk_test_" + Guid.NewGuid().ToString("N")[..8], FilteredSearch = strategy };
        await using var store = PgVectorStore.Create(builder.ConnectionString, new HashEmbeddings(), options);
        var documents = Enumerable.Range(0, 600).Select(i => new VectorDocument($"d{i}", $"texto {i}")
        {
            Embedding = HashEmbeddings.Vector($"d{i}"),
            Metadata = new Dictionary<string, string> { ["modelo"] = "m1", ["usuario"] = $"u{i % 100}", ["coleccion"] = i % 2 == 0 ? "par" : "impar" },
        }).ToList();
        await store.UpsertAsync(documents, ct);

        var query = HashEmbeddings.Vector("consulta");
        List<string> Expected(Func<VectorDocument, bool> where, int k) =>
            documents.Where(where).OrderBy(d => Cosine(d.Embedding!, query)).Take(k).Select(d => d.Id).ToList();

        // Selectivo: 6 filas del usuario 7, se piden 5.
        var selective = VectorFilter.Eq("modelo", "m1") & VectorFilter.Eq("usuario", "u7");
        var hits = await store.SearchAsync("consulta", topK: 5, filter: selective, cancellationToken: ct);
        Assert.Equal(Expected(d => d.Metadata!["usuario"] == "u7", 5), hits.Select(h => h.Document.Id));
        Assert.All(hits, h => Assert.InRange(h.Score, -1, 1));

        // In y Not también se resuelven completos.
        var some = VectorFilter.In("usuario", "u3", "u4") & !VectorFilter.Eq("coleccion", "par");
        hits = await store.SearchAsync("consulta", topK: 10, filter: some, cancellationToken: ct);
        Assert.Equal(Expected(d => d.Metadata!["usuario"] is "u3" or "u4" && d.Metadata["coleccion"] != "par", 10), hits.Select(h => h.Document.Id));

        // Poco selectivo: el HNSW basta (Auto) o la búsqueda exacta ordena la mitad de la tabla (Exact).
        hits = await store.SearchAsync("consulta", topK: 5, filter: VectorFilter.Eq("coleccion", "par"), cancellationToken: ct);
        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Equal("par", h.Document.Metadata!["coleccion"]));

        // La parte semántica de la híbrida usa la misma estrategia.
        var hybrid = await store.HybridSearchAsync("consulta", topK: 3, filter: selective, cancellationToken: ct);
        Assert.Equal(Expected(d => d.Metadata!["usuario"] == "u7", 3), hybrid.Select(h => h.Document.Id));

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connection!);
        await using var drop = dataSource.CreateCommand($"DROP TABLE \"{options.Table}\"");
        await drop.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task TranslatedFiltersCanUseTheMetadataIndex()
    {
        var connection = Environment.GetEnvironmentVariable("AISDK_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connection), "AISDK_TEST_PG no configurada");
        var ct = TestContext.Current.CancellationToken;
        var table = "aisdk_test_" + Guid.NewGuid().ToString("N")[..8];
        await using (var store = PgVectorStore.Create(connection!, new HashEmbeddings(), new PgVectorStoreOptions { Dimensions = 32, Table = table }))
        {
            await store.EnsureCreatedAsync(ct);
        }

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connection!);
        foreach (var filter in new[]
        {
            VectorFilter.Eq("usuario", "u7") & VectorFilter.Eq("modelo", "m1"),
            VectorFilter.In("usuario", "u7", "u8"),
            VectorFilter.Eq("a", "1") | VectorFilter.Eq("b", "2"),
        })
        {
            var parameters = new List<object>();
            var where = PgVectorFilterTranslator.Translate(filter, parameters);
            await using var batch = dataSource.CreateBatch();
            batch.BatchCommands.Add(new Npgsql.NpgsqlBatchCommand("SET enable_seqscan = off"));
            var explain = new Npgsql.NpgsqlBatchCommand($"EXPLAIN SELECT id FROM \"{table}\" WHERE {where}");
            foreach (var p in parameters)
            {
                explain.Parameters.Add(new Npgsql.NpgsqlParameter { Value = p });
            }

            batch.BatchCommands.Add(explain);
            var plan = new System.Text.StringBuilder();
            await using (var reader = await batch.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    plan.AppendLine(reader.GetString(0));
                }
            }

            Assert.Contains($"{table}_metadata_idx", plan.ToString(), StringComparison.Ordinal);
        }

        await using var drop = dataSource.CreateCommand($"DROP TABLE \"{table}\"");
        await drop.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task WorksAsAgentTool()
    {
        await using var store = await CreateStoreAsync();
        var model = new AiSdk.Testing.MockLanguageModel()
            .ReturnsToolCall("search_knowledge_base", new { query = "envio gratis" })
            .ReturnsText("El envío es gratis sobre $30.000.");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "¿El envío tiene costo?",
            Tools = [store.AsTool(topK: 1)],
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.Equal("d2", result.Steps[0].ToolResults[0].Output[0].GetProperty("id").GetString());
    }
}
