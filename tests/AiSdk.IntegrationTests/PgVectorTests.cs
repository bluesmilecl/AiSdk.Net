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
