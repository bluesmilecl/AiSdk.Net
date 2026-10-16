using System.ComponentModel;
using System.Text;

namespace AiSdk;

/// <summary>Documento indexado en un almacén vectorial.</summary>
public sealed record VectorDocument(string Id, string Text)
{
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Embedding precalculado (si es null, el almacén lo calcula).</summary>
    public float[]? Embedding { get; init; }
}

public sealed record VectorSearchResult(VectorDocument Document, double Score);

/// <summary>Almacén vectorial para RAG. Implementa uno propio sobre pgvector, Qdrant, Azure AI Search...</summary>
public interface IVectorStore
{
    Task UpsertAsync(IEnumerable<VectorDocument> documents, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>Almacén vectorial en memoria (búsqueda exacta por similitud coseno).</summary>
public sealed class InMemoryVectorStore(IEmbeddingModel embeddingModel) : IVectorStore
{
    private readonly Dictionary<string, (VectorDocument Document, float[] Embedding)> _items = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public async Task UpsertAsync(IEnumerable<VectorDocument> documents, CancellationToken cancellationToken = default)
    {
        var list = documents.ToList();
        var missing = list.Where(d => d.Embedding is null).ToList();
        var computed = missing.Count == 0
            ? []
            : (await Ai.EmbedManyAsync(embeddingModel, missing.Select(d => d.Text).ToList(), cancellationToken).ConfigureAwait(false)).Embeddings;

        lock (_gate)
        {
            var next = 0;
            foreach (var doc in list)
            {
                var embedding = doc.Embedding ?? computed[next++];
                _items[doc.Id] = (doc with { Embedding = embedding }, embedding);
            }
        }
    }

    /// <summary>Atajo: divide textos en fragmentos y los indexa.</summary>
    public Task AddTextsAsync(IEnumerable<(string Id, string Text)> texts, int chunkSize = 1000, int overlap = 100, CancellationToken cancellationToken = default) =>
        UpsertAsync(
            texts.SelectMany(t => TextChunker.Split(t.Text, chunkSize, overlap)
                .Select((chunk, i) => new VectorDocument($"{t.Id}#{i}", chunk)
                {
                    Metadata = new Dictionary<string, string> { ["source"] = t.Id, ["chunk"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                })),
            cancellationToken);

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var queryEmbedding = (await Ai.EmbedAsync(embeddingModel, query, cancellationToken).ConfigureAwait(false)).Embedding;
        List<(VectorDocument Document, float[] Embedding)> snapshot;
        lock (_gate)
        {
            snapshot = [.. _items.Values];
        }

        return snapshot
            .Where(i => filter is null || filter.Matches(i.Document.Metadata))
            .Select(i => new VectorSearchResult(i.Document, Ai.CosineSimilarity(queryEmbedding, i.Embedding)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    public Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in ids)
            {
                _items.Remove(id);
            }
        }

        return Task.CompletedTask;
    }
}

public static class VectorStoreExtensions
{
    /// <summary>
    /// Expone el almacén como herramienta de búsqueda para que el agente consulte la base de
    /// conocimiento cuando lo necesite (RAG agéntico).
    /// </summary>
    public static Tool AsTool(
        this IVectorStore store,
        string name = "search_knowledge_base",
        string description = "Busca información relevante en la base de conocimiento. Úsala antes de responder preguntas sobre los documentos.",
        int topK = 5,
        IRerankingModel? reranker = null,
        VectorFilter? filter = null,
        bool hybrid = false) =>
        Tool.Create<KnowledgeQuery, IReadOnlyList<KnowledgeHit>>(name, description, async (input, ct) =>
        {
            var take = reranker is null ? topK : topK * 3;
            // Búsqueda híbrida (vectores + palabras clave) si el almacén la soporta.
            var hits = hybrid && store is IHybridVectorStore hybridStore
                ? await hybridStore.HybridSearchAsync(input.Query, take, filter, ct).ConfigureAwait(false)
                : await store.SearchAsync(input.Query, take, filter, ct).ConfigureAwait(false);
            if (reranker is not null && hits.Count > 0)
            {
                var reranked = await Ai.RerankAsync(reranker, input.Query, hits, h => h.Document.Text, topK, ct).ConfigureAwait(false);
                hits = reranked.Ranking.Select(r => r.Document with { Score = r.Score }).ToList();
            }

            return hits.Select(h => new KnowledgeHit(h.Document.Id, h.Document.Text, Math.Round(h.Score, 4), h.Document.Metadata)).ToList();
        });

    /// <summary>Busca y formatea los resultados como contexto para un prompt (RAG clásico).</summary>
    public static async Task<string> BuildContextAsync(this IVectorStore store, string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var hits = await store.SearchAsync(query, topK, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            sb.Append("[").Append(hit.Document.Id).Append("]\n").Append(hit.Document.Text).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    private sealed record KnowledgeQuery([property: Description("Consulta de búsqueda en lenguaje natural.")] string Query);

    private sealed record KnowledgeHit(string Id, string Text, double Score, IReadOnlyDictionary<string, string>? Metadata);
}

/// <summary>Divide texto en fragmentos para indexarlo.</summary>
public static class TextChunker
{
    /// <summary>
    /// Divide por párrafos y oraciones intentando no superar <paramref name="maxChars"/>, con
    /// <paramref name="overlap"/> caracteres repetidos entre fragmentos consecutivos.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxChars = 1000, int overlap = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        overlap = Math.Clamp(overlap, 0, maxChars / 2);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var piece in Pieces(text, maxChars))
        {
            if (current.Length > 0 && current.Length + piece.Length > maxChars)
            {
                chunks.Add(current.ToString().Trim());
                var tail = overlap > 0 ? current.ToString()[Math.Max(0, current.Length - overlap)..] : string.Empty;
                current.Clear().Append(tail);
            }

            current.Append(piece);
        }

        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            chunks.Add(last);
        }

        return chunks;
    }

    private static IEnumerable<string> Pieces(string text, int maxChars)
    {
        foreach (var paragraph in text.Split("\n\n"))
        {
            var withBreak = paragraph + "\n\n";
            if (withBreak.Length <= maxChars)
            {
                yield return withBreak;
                continue;
            }

            // Párrafo largo: por oraciones y, si hace falta, por longitud fija.
            foreach (var sentence in System.Text.RegularExpressions.Regex.Split(paragraph, @"(?<=[\.\!\?])\s+"))
            {
                for (var i = 0; i < sentence.Length; i += maxChars)
                {
                    yield return sentence.Substring(i, Math.Min(maxChars, sentence.Length - i)) + " ";
                }
            }
        }
    }
}
