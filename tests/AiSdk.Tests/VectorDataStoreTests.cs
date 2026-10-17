using AiSdk.VectorData;
using SkInMemory = CommunityToolkit.VectorData.InMemory.InMemoryVectorStore;

namespace AiSdk.Tests;

public class VectorDataStoreTests
{
    private sealed class KeywordEmbeddings : IEmbeddingModel
    {
        public static readonly string[] Vocabulary = ["devolucion", "envio", "horario", "garantia"];

        public string Provider => "test";

        public string ModelId => "kw";

        public int? MaxEmbeddingsPerCall => null;

        public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingModelResult(
                values.Select(v => Vocabulary.Select(w => v.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1f : 0.05f).ToArray()).ToList(),
                null));
    }

    private static async Task<VectorDataStore> CreateStoreAsync()
    {
        var store = new VectorDataStore(new SkInMemory(), new KeywordEmbeddings(), new VectorDataStoreOptions
        {
            CollectionName = "docs",
            Dimensions = KeywordEmbeddings.Vocabulary.Length,
            FilterableFields = ["tipo", "pais"],
            IndexKind = null,
        });
        await store.UpsertAsync(
        [
            new VectorDocument("d1", "Política de devolucion: 30 días.") { Metadata = new Dictionary<string, string> { ["tipo"] = "politica", ["pais"] = "CL", ["autor"] = "ana" } },
            new VectorDocument("d2", "El envio es gratis.") { Metadata = new Dictionary<string, string> { ["tipo"] = "politica", ["pais"] = "PE" } },
            new VectorDocument("d3", "Horario de atención.") { Metadata = new Dictionary<string, string> { ["tipo"] = "faq" } },
        ]);
        return store;
    }

    [Fact]
    public async Task UpsertSearchAndDelete()
    {
        var store = await CreateStoreAsync();

        var hits = await store.SearchAsync("¿cómo hago una devolucion?", topK: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("d1", hits[0].Document.Id);
        Assert.Equal("ana", hits[0].Document.Metadata!["autor"]); // metadatos no indexados se conservan

        await store.DeleteAsync(["d1"], TestContext.Current.CancellationToken);
        var after = await store.SearchAsync("devolucion", topK: 5, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(after, h => h.Document.Id == "d1");
    }

    [Fact]
    public async Task FiltersAreTranslatedToConnectorExpressions()
    {
        var store = await CreateStoreAsync();

        var peru = await store.SearchAsync("devolucion", topK: 5, filter: VectorFilter.Eq("pais", "PE"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["d2"], peru.Select(h => h.Document.Id));

        var combined = await store.SearchAsync("x", topK: 5, filter: VectorFilter.In("pais", "CL", "PE") & !VectorFilter.Eq("pais", "CL"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["d2"], combined.Select(h => h.Document.Id));

        var withoutCountry = await store.SearchAsync("x", topK: 5, filter: !VectorFilter.Exists("pais"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["d3"], withoutCountry.Select(h => h.Document.Id));

        // Sin búsqueda híbrida nativa, cae a la semántica.
        var hybrid = await store.HybridSearchAsync("envio", topK: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("d2", hybrid[0].Document.Id);
    }

    [Fact]
    public void UndeclaredFieldsCannotBeFiltered()
    {
        var store = new VectorDataStore(new SkInMemory(), new KeywordEmbeddings(), new VectorDataStoreOptions { CollectionName = "c", Dimensions = 4, FilterableFields = ["tipo"] });
        Assert.Throws<NotSupportedException>(() => store.ToExpression(VectorFilter.Eq("autor", "ana")));
        Assert.Throws<ArgumentException>(() => new VectorDataStore(new SkInMemory(), new KeywordEmbeddings(), new VectorDataStoreOptions { CollectionName = "c", Dimensions = 4, FilterableFields = ["Text"] }));
    }
}
