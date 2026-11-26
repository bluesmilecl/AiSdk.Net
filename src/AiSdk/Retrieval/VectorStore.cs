using System.ComponentModel;
using System.Text;
using AiSdk.Resources;

namespace AiSdk;

/// <summary>Document indexed in a vector store.</summary>
public sealed record VectorDocument(string Id, string Text)
{
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Gets the precomputed embedding (if null, the store computes it).</summary>
    public float[]? Embedding { get; init; }
}

public sealed record VectorSearchResult(VectorDocument Document, double Score);

/// <summary>Vector store for RAG. Implement your own on top of pgvector, Qdrant, Azure AI Search...</summary>
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

/// <summary>In-memory vector store (exact search by cosine similarity).</summary>
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

    /// <summary>Shortcut: splits texts into chunks and indexes them.</summary>
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
    /// Exposes the store as a search tool so the agent can query the knowledge
    /// base when it needs to (agentic RAG). Without <paramref name="description"/>, a localized default is used.
    /// </summary>
    public static Tool AsTool(
        this IVectorStore store,
        string name = "search_knowledge_base",
        string? description = null,
        int topK = 5,
        IRerankingModel? reranker = null,
        VectorFilter? filter = null,
        bool hybrid = false) =>
        Tool.Create<KnowledgeQuery, IReadOnlyList<KnowledgeHit>>(name, description ?? Strings.Knowledge_ToolDescription, async (input, ct) =>
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

    /// <summary>Searches and formats the results as context for a prompt (classic RAG).</summary>
    public static async Task<string> BuildContextAsync(this IVectorStore store, string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var hits = await store.SearchAsync(query, topK, cancellationToken: cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            sb.Append('[').Append(hit.Document.Id).Append("]\n").Append(hit.Document.Text).Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    private sealed record KnowledgeQuery([property: LocalizedDescription(typeof(Strings), nameof(Strings.Knowledge_Param_Query))] string Query);

    private sealed record KnowledgeHit(string Id, string Text, double Score, IReadOnlyDictionary<string, string>? Metadata);
}

/// <summary>Splits text into chunks for indexing.</summary>
public static class TextChunker
{
    /// <summary>
    /// Splits by paragraphs and sentences, trying not to exceed <paramref name="maxChars"/>, with
    /// <paramref name="overlap"/> characters repeated between consecutive chunks.
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
                    yield return string.Concat(sentence.AsSpan(i, Math.Min(maxChars, sentence.Length - i)), " ");
                }
            }
        }
    }
}
