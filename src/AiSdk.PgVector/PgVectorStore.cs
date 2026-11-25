using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Pgvector.Npgsql;

namespace AiSdk.PgVector;

/// <summary>pgvector distance metric.</summary>
public enum PgVectorDistance
{
    /// <summary>Cosine distance (<c>&lt;=&gt;</c>), the usual choice for text embeddings.</summary>
    Cosine,

    /// <summary>Euclidean distance (<c>&lt;-&gt;</c>).</summary>
    Euclidean,

    /// <summary>Negative inner product (<c>&lt;#&gt;</c>), for normalized embeddings.</summary>
    InnerProduct,
}

/// <summary>How a semantic search with a <see cref="VectorFilter"/> is resolved.</summary>
/// <remarks>
/// The HNSW index returns at most <c>hnsw.ef_search</c> candidates (40 by default) and the filter is applied afterwards:
/// a selective filter (one user, one customer) can leave the search with fewer results than requested, or with
/// none, even when there are rows that match it.
/// </remarks>
public enum PgVectorFilteredSearch
{
    /// <summary>
    /// HNSW index and, if the filter leaves fewer than <c>topK</c> results (or fewer than <c>hnsw.ef_search</c> when
    /// <c>topK</c> is larger), an exact search over the rows that match the filter, located with the metadata GIN
    /// index. It is a single query: the exact part is not executed unless needed. Its cost grows with the rows that
    /// match the indexable part of the filter (equalities and <c>In</c>; not <c>Not</c> or <c>Exists</c>): on very
    /// large tables with filters that are not very selective in that part, consider <see cref="Approximate"/> and raising
    /// <c>hnsw.ef_search</c>.
    /// </summary>
    Auto,

    /// <summary>Only the HNSW index with the filter applied afterwards (fastest; may return fewer results).</summary>
    Approximate,

    /// <summary>
    /// Always an exact search over the rows that match the filter (complete results; the cost grows with the
    /// number of matching rows). Without a filter, the HNSW index is used.
    /// </summary>
    Exact,
}

/// <summary>Options for <see cref="PgVectorStore"/>.</summary>
public sealed class PgVectorStoreOptions
{
    /// <summary>Embedding dimensions (e.g. 1536 for text-embedding-3-small).</summary>
    public required int Dimensions { get; init; }

    public string Schema { get; init; } = "public";

    public string Table { get; init; } = "aisdk_documents";

    public PgVectorDistance Distance { get; init; } = PgVectorDistance.Cosine;

    /// <summary>Full-text search configuration for hybrid search (<c>simple</c>, <c>spanish</c>, <c>english</c>...).</summary>
    public string TextSearchConfig { get; init; } = "simple";

    /// <summary>Creates the extension, the table and the indexes (HNSW, GIN) if they do not exist.</summary>
    public bool CreateIfNotExists { get; init; } = true;

    /// <summary>Reciprocal Rank Fusion constant used to combine rankings in hybrid search.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Strategy for filtered semantic searches (also the semantic part of hybrid search).</summary>
    public PgVectorFilteredSearch FilteredSearch { get; init; } = PgVectorFilteredSearch.Auto;
}

/// <summary>
/// Vector store on PostgreSQL + pgvector. Stores text, metadata (jsonb) and the embedding,
/// filters in SQL and offers hybrid search with Reciprocal Rank Fusion. Equality filters are
/// translated to jsonb containment (<c>@&gt;</c>), which uses the metadata GIN index; see
/// <see cref="PgVectorStoreOptions.FilteredSearch"/>.
/// </summary>
public sealed partial class PgVectorStore : IHybridVectorStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly IEmbeddingModel _embeddings;
    private readonly PgVectorStoreOptions _options;
    private readonly string _table;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <param name="dataSource">Data source created with <c>NpgsqlDataSourceBuilder.UseVector()</c>.</param>
    /// <param name="embeddings">Embedding model used for indexing and querying.</param>
    /// <param name="options">Store options.</param>
    public PgVectorStore(NpgsqlDataSource dataSource, IEmbeddingModel embeddings, PgVectorStoreOptions options)
        : this(dataSource, embeddings, options, ownsDataSource: false)
    {
    }

    private PgVectorStore(NpgsqlDataSource dataSource, IEmbeddingModel embeddings, PgVectorStoreOptions options, bool ownsDataSource)
    {
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _embeddings = embeddings;
        _options = options;
        _table = $"{Identifier(options.Schema)}.{Identifier(options.Table)}";
        if (!IdentifierRegex().IsMatch(options.TextSearchConfig))
        {
            throw new ArgumentException(Resources.Strings.InvalidTextSearchConfig, nameof(options));
        }
    }

    /// <summary>Creates the store from a connection string (registers the <c>vector</c> type).</summary>
    public static PgVectorStore Create(string connectionString, IEmbeddingModel embeddings, PgVectorStoreOptions options)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return new PgVectorStore(builder.Build(), embeddings, options, ownsDataSource: true);
    }

    /// <summary>Creates the extension, table and indexes if they do not exist (called automatically on first use).</summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !_options.CreateIfNotExists)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            const string EmptyJson = "'{}'::jsonb";
            var ops = _options.Distance switch
            {
                PgVectorDistance.Euclidean => "vector_l2_ops",
                PgVectorDistance.InnerProduct => "vector_ip_ops",
                _ => "vector_cosine_ops",
            };
            var sql = $"""
                CREATE EXTENSION IF NOT EXISTS vector;
                CREATE TABLE IF NOT EXISTS {_table} (
                    id text PRIMARY KEY,
                    content text NOT NULL,
                    metadata jsonb NOT NULL DEFAULT {EmptyJson},
                    embedding vector({_options.Dimensions}) NOT NULL,
                    content_search tsvector GENERATED ALWAYS AS (to_tsvector('{_options.TextSearchConfig}', content)) STORED,
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS {Identifier(_options.Table + "_embedding_idx")} ON {_table} USING hnsw (embedding {ops});
                CREATE INDEX IF NOT EXISTS {Identifier(_options.Table + "_metadata_idx")} ON {_table} USING gin (metadata jsonb_path_ops);
                CREATE INDEX IF NOT EXISTS {Identifier(_options.Table + "_search_idx")} ON {_table} USING gin (content_search);
                """;
            await using (var cmd = _dataSource.CreateCommand(sql))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // La extensión puede haberse creado en esta misma conexión: se recargan los tipos.
            await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.ReloadTypesAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertAsync(IEnumerable<VectorDocument> documents, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var list = documents.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var missing = list.Where(d => d.Embedding is null).ToList();
        var computed = missing.Count == 0
            ? []
            : (await Ai.EmbedManyAsync(_embeddings, missing.Select(d => d.Text).ToList(), cancellationToken).ConfigureAwait(false)).Embeddings;

        var next = 0;
        await using var batch = _dataSource.CreateBatch();
        foreach (var doc in list)
        {
            var embedding = doc.Embedding ?? computed[next++];
            if (embedding.Length != _options.Dimensions)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.EmbeddingDimensionsMismatch, doc.Id, embedding.Length, _options.Dimensions));
            }

            var command = new NpgsqlBatchCommand($"""
                INSERT INTO {_table} (id, content, metadata, embedding)
                VALUES ($1, $2, $3, $4)
                ON CONFLICT (id) DO UPDATE SET content = EXCLUDED.content, metadata = EXCLUDED.metadata,
                    embedding = EXCLUDED.embedding, updated_at = now()
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = doc.Id });
            command.Parameters.Add(new NpgsqlParameter { Value = doc.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(doc.Metadata ?? new Dictionary<string, string>()), NpgsqlDbType = NpgsqlDbType.Jsonb });
            command.Parameters.Add(new NpgsqlParameter { Value = new Vector(embedding) });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var vector = new Vector((await Ai.EmbedAsync(_embeddings, query, cancellationToken).ConfigureAwait(false)).Embedding);
        var parameters = new List<object> { vector, topK };
        var filterSql = filter is null ? null : PgVectorFilterTranslator.Translate(filter, parameters);
        var sql = $"""
            WITH {SemanticHits(filterSql, "$2")}
            SELECT t.id, t.content, t.metadata::text, {ScoreExpression("h.distance")} AS score
            FROM hits h JOIN {_table} t ON t.id = h.id
            ORDER BY h.distance
            LIMIT $2
            """;
        return await ReadResultsAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// CTEs ending in <c>hits(id, distance)</c>: the <paramref name="limit"/> documents closest to
    /// <c>$1</c> that match the filter, according to <see cref="PgVectorStoreOptions.FilteredSearch"/>.
    /// </summary>
    private string SemanticHits(string? filterSql, string limit)
    {
        var op = Operator;
        var approximate = $"""
            SELECT id, embedding {op} $1 AS distance
                FROM {_table}
                {(filterSql is null ? string.Empty : "WHERE " + filterSql)}
                ORDER BY embedding {op} $1
                LIMIT {limit}
            """;
        if (filterSql is null || _options.FilteredSearch == PgVectorFilteredSearch.Approximate)
        {
            return $"hits AS ({approximate})";
        }

        // MATERIALIZED impide que el planificador vuelva a ordenar por el índice HNSW (que filtra después): la parte
        // exacta busca las filas que cumplen el filtro (índice GIN de metadatos) y ordena solo esas.
        if (_options.FilteredSearch == PgVectorFilteredSearch.Exact)
        {
            return $"""
                filtered AS MATERIALIZED (SELECT id, embedding {op} $1 AS distance FROM {_table} WHERE {filterSql}),
                hits AS (SELECT id, distance FROM filtered ORDER BY distance LIMIT {limit})
                """;
        }

        // Auto: el HNSW basta si entregó todo lo que podía (topK, o ef_search si topK es mayor); si el filtro le quitó
        // candidatos, la condición sin variables se evalúa una sola vez y habilita la parte exacta.
        const string EfSearch = "COALESCE(NULLIF(current_setting('hnsw.ef_search', true), '')::int, 40)";
        return $"""
            approx AS MATERIALIZED ({approximate}),
            filtered AS MATERIALIZED (
                SELECT id, embedding {op} $1 AS distance FROM {_table}
                WHERE {filterSql} AND (SELECT count(*) FROM approx) < LEAST({limit}, {EfSearch})
            ),
            hits AS (
                SELECT id, distance FROM approx WHERE (SELECT count(*) FROM approx) >= LEAST({limit}, {EfSearch})
                UNION ALL
                (SELECT id, distance FROM filtered ORDER BY distance LIMIT {limit})
            )
            """;
    }

    /// <summary>
    /// Hybrid search: combines the semantic ranking (pgvector) and the full-text ranking
    /// (<c>websearch_to_tsquery</c>) with Reciprocal Rank Fusion.
    /// </summary>
    public async Task<IReadOnlyList<VectorSearchResult>> HybridSearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var vector = new Vector((await Ai.EmbedAsync(_embeddings, query, cancellationToken).ConfigureAwait(false)).Embedding);
        var parameters = new List<object> { vector, query, topK * 4, topK, _options.RrfK };
        // En "keyword", metadata (sin calificar) es la de d: la función de texto completo solo aporta la columna q.
        var filterSql = filter is null ? null : PgVectorFilterTranslator.Translate(filter, parameters);
        var config = _options.TextSearchConfig;
        var sql = $"""
            WITH {SemanticHits(filterSql, "$3")},
            semantic AS (
                SELECT id, ROW_NUMBER() OVER (ORDER BY distance) AS rank FROM hits
            ),
            keyword AS (
                SELECT d.id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(d.content_search, q) DESC) AS rank
                FROM {_table} d, websearch_to_tsquery('{config}', $2) q
                WHERE d.content_search @@ q AND {filterSql ?? "TRUE"}
                ORDER BY ts_rank_cd(d.content_search, q) DESC
                LIMIT $3
            ),
            fused AS (
                SELECT COALESCE(s.id, k.id) AS id,
                       COALESCE(1.0 / ($5 + s.rank), 0) + COALESCE(1.0 / ($5 + k.rank), 0) AS score
                FROM semantic s FULL OUTER JOIN keyword k ON s.id = k.id
            )
            SELECT t.id, t.content, t.metadata::text, f.score::float8
            FROM fused f JOIN {_table} t ON t.id = f.id
            ORDER BY f.score DESC
            LIMIT $4
            """;
        return await ReadResultsAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _dataSource.CreateCommand($"DELETE FROM {_table} WHERE id = ANY($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = ids.ToArray() });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes all documents that match the filter (e.g. when reindexing a source).</summary>
    public async Task<int> DeleteWhereAsync(VectorFilter filter, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new List<object>();
        var where = PgVectorFilterTranslator.Translate(filter, parameters);
        await using var cmd = _dataSource.CreateCommand($"DELETE FROM {_table} WHERE {where}");
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Number of indexed documents.</summary>
    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = _dataSource.CreateCommand($"SELECT count(*) FROM {_table}");
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private string Operator => _options.Distance switch
    {
        PgVectorDistance.Euclidean => "<->",
        PgVectorDistance.InnerProduct => "<#>",
        _ => "<=>",
    };

    /// <summary>Score (higher = more similar) derived from the distance of the <see cref="PgVectorStoreOptions.Distance"/> operator.</summary>
    private string ScoreExpression(string distance) => _options.Distance switch
    {
        PgVectorDistance.Euclidean => $"(1.0 / (1.0 + {distance}))::float8",
        PgVectorDistance.InnerProduct => $"({distance} * -1)::float8",
        _ => $"(1 - {distance})::float8",
    };

    private async Task<IReadOnlyList<VectorSearchResult>> ReadResultsAsync(string sql, List<object> parameters, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        }

        var results = new List<VectorSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2));
            results.Add(new VectorSearchResult(
                new VectorDocument(reader.GetString(0), reader.GetString(1)) { Metadata = metadata is { Count: > 0 } ? metadata : null },
                reader.GetDouble(3)));
        }

        return results;
    }

    private static string Identifier(string name) =>
        IdentifierRegex().IsMatch(name) ? $"\"{name}\"" : throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.InvalidSqlIdentifier, name));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex IdentifierRegex();

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Translates a <see cref="VectorFilter"/> into SQL over the <c>metadata</c> (jsonb) column, with positional parameters.
/// </summary>
/// <remarks>
/// Equalities are written as jsonb containment (<c>metadata @&gt; '{"key":"value"}'</c>), which uses the
/// <c>jsonb_path_ops</c> GIN index created by <see cref="PgVectorStore"/>; those in the same <c>And</c> are merged into one object.
/// <see cref="VectorFilter.In"/> uses <c>@&gt; ANY(...)</c> (also indexable) and <see cref="VectorFilter.Exists"/> the
/// <c>?</c> operator (indexable with a <c>jsonb_ops</c> GIN index). Metadata is compared as JSON text: that is what
/// <see cref="PgVectorStore.UpsertAsync"/> stores; a numeric or boolean value written by another tool does not match
/// <see cref="VectorFilter.Eq"/>.
/// </remarks>
public static class PgVectorFilterTranslator
{
    /// <summary>Returns the SQL condition and adds its values to <paramref name="parameters"/> (indexes continue the numbering).</summary>
    public static string Translate(VectorFilter filter, List<object> parameters)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(parameters);
        return Visit(filter, parameters);
    }

    private static string Visit(VectorFilter filter, List<object> parameters) => filter switch
    {
        EqFilter eq => Contains([new(eq.Key, eq.Value)], parameters),
        InFilter { Values.Count: 0 } => "FALSE",
        InFilter @in when @in.Values.Distinct().Count() == 1 => Contains([new(@in.Key, @in.Values[0])], parameters),
        InFilter @in => $"metadata @> ANY({Param(@in.Values.Distinct().Select(v => Json([new(@in.Key, v)])).ToArray(), parameters)}::jsonb[])",
        ExistsFilter ex => $"metadata ? {Param(ex.Key, parameters)}",
        AndFilter and => And(and, parameters),
        OrFilter or => Join(Flatten<OrFilter>(or, o => o.Filters).Select(f => Visit(f, parameters)).ToList(), " OR ", "FALSE"),
        NotFilter not => $"NOT ({Visit(not.Inner, parameters)})",
        _ => throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.FilterNotSupported, filter.GetType().Name)),
    };

    /// <summary>The And equalities (including nested ones) go into a single <c>@&gt;</c>: one index lookup.</summary>
    private static string And(AndFilter and, List<object> parameters)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var rest = new List<VectorFilter>();
        foreach (var filter in Flatten<AndFilter>(and, a => a.Filters))
        {
            var pair = filter switch
            {
                EqFilter eq => new KeyValuePair<string, string>(eq.Key, eq.Value),
                InFilter @in when @in.Values.Count > 0 && @in.Values.Distinct().Count() == 1 => new(@in.Key, @in.Values[0]),
                _ => (KeyValuePair<string, string>?)null,
            };

            // Una clave repetida (p. ej. Eq("a","1") & Eq("a","2")) no cabe en un objeto JSON: va aparte.
            if (pair is { } p && !pairs.Exists(x => x.Key == p.Key))
            {
                pairs.Add(p);
            }
            else
            {
                rest.Add(filter);
            }
        }

        var conditions = new List<string>();
        if (pairs.Count > 0)
        {
            conditions.Add(Contains(pairs, parameters));
        }

        conditions.AddRange(rest.Select(f => Visit(f, parameters)));
        return Join(conditions, " AND ", "TRUE");
    }

    private static IEnumerable<VectorFilter> Flatten<T>(T filter, Func<T, IReadOnlyList<VectorFilter>> children)
        where T : VectorFilter =>
        children(filter).SelectMany(f => f is T same ? Flatten(same, children) : [f]);

    private static string Join(List<string> conditions, string separator, string empty) => conditions.Count switch
    {
        0 => empty,
        1 => conditions[0],
        _ => "(" + string.Join(separator, conditions) + ")",
    };

    private static string Contains(List<KeyValuePair<string, string>> pairs, List<object> parameters) =>
        $"metadata @> {Param(Json(pairs), parameters)}::jsonb";

    private static string Param(object value, List<object> parameters)
    {
        parameters.Add(value);
        return "$" + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Json(List<KeyValuePair<string, string>> pairs)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in pairs)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
