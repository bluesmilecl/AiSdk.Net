using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Pgvector.Npgsql;

namespace AiSdk.PgVector;

/// <summary>Métrica de distancia de pgvector.</summary>
public enum PgVectorDistance
{
    /// <summary>Distancia coseno (<c>&lt;=&gt;</c>), la habitual con embeddings de texto.</summary>
    Cosine,

    /// <summary>Distancia euclidiana (<c>&lt;-&gt;</c>).</summary>
    Euclidean,

    /// <summary>Producto interno negativo (<c>&lt;#&gt;</c>), para embeddings normalizados.</summary>
    InnerProduct,
}

/// <summary>Opciones de <see cref="PgVectorStore"/>.</summary>
public sealed class PgVectorStoreOptions
{
    /// <summary>Dimensiones de los embeddings (p. ej. 1536 para text-embedding-3-small).</summary>
    public required int Dimensions { get; init; }

    public string Schema { get; init; } = "public";

    public string Table { get; init; } = "aisdk_documents";

    public PgVectorDistance Distance { get; init; } = PgVectorDistance.Cosine;

    /// <summary>Configuración de texto completo para la búsqueda híbrida (<c>simple</c>, <c>spanish</c>, <c>english</c>...).</summary>
    public string TextSearchConfig { get; init; } = "simple";

    /// <summary>Crea la extensión, la tabla y los índices (HNSW, GIN) si no existen.</summary>
    public bool CreateIfNotExists { get; init; } = true;

    /// <summary>Constante de Reciprocal Rank Fusion para combinar rankings en la búsqueda híbrida.</summary>
    public int RrfK { get; init; } = 60;
}

/// <summary>
/// Almacén vectorial sobre PostgreSQL + pgvector. Guarda texto, metadatos (jsonb) y embedding,
/// filtra en SQL y ofrece búsqueda híbrida con Reciprocal Rank Fusion.
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

    /// <param name="dataSource">Fuente de datos creada con <c>NpgsqlDataSourceBuilder.UseVector()</c>.</param>
    /// <param name="embeddings">Modelo de embeddings usado para indexar y consultar.</param>
    /// <param name="options">Opciones del almacén.</param>
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
            throw new ArgumentException("TextSearchConfig inválido.", nameof(options));
        }
    }

    /// <summary>Crea el almacén a partir de una cadena de conexión (registra el tipo <c>vector</c>).</summary>
    public static PgVectorStore Create(string connectionString, IEmbeddingModel embeddings, PgVectorStoreOptions options)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return new PgVectorStore(builder.Build(), embeddings, options, ownsDataSource: true);
    }

    /// <summary>Crea extensión, tabla e índices si no existen (se llama automáticamente en el primer uso).</summary>
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
                throw new ArgumentException($"El embedding de '{doc.Id}' tiene {embedding.Length} dimensiones; la tabla espera {_options.Dimensions}.");
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
        var where = filter is null ? string.Empty : "WHERE " + PgVectorFilterTranslator.Translate(filter, parameters);
        var op = Operator;
        var sql = $"""
            SELECT id, content, metadata::text, {ScoreExpression("embedding", "$1")} AS score
            FROM {_table}
            {where}
            ORDER BY embedding {op} $1
            LIMIT $2
            """;
        return await ReadResultsAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Búsqueda híbrida: combina el ranking semántico (pgvector) y el de texto completo
    /// (<c>websearch_to_tsquery</c>) con Reciprocal Rank Fusion.
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
        var filterSql = filter is null ? "TRUE" : PgVectorFilterTranslator.Translate(filter, parameters);
        var config = _options.TextSearchConfig;
        var sql = $"""
            WITH semantic AS (
                SELECT id, ROW_NUMBER() OVER (ORDER BY embedding {Operator} $1) AS rank
                FROM {_table}
                WHERE {filterSql}
                ORDER BY embedding {Operator} $1
                LIMIT $3
            ),
            keyword AS (
                SELECT d.id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(d.content_search, q) DESC) AS rank
                FROM {_table} d, websearch_to_tsquery('{config}', $2) q
                WHERE d.content_search @@ q AND {filterSql.Replace("metadata", "d.metadata", StringComparison.Ordinal)}
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

    /// <summary>Elimina todos los documentos que cumplen el filtro (p. ej. al reindexar una fuente).</summary>
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

    /// <summary>Cantidad de documentos indexados.</summary>
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

    /// <summary>Puntuación en la que más alto = más parecido.</summary>
    private string ScoreExpression(string column, string parameter) => _options.Distance switch
    {
        PgVectorDistance.Euclidean => $"(1.0 / (1.0 + ({column} <-> {parameter})))::float8",
        PgVectorDistance.InnerProduct => $"(({column} <#> {parameter}) * -1)::float8",
        _ => $"(1 - ({column} <=> {parameter}))::float8",
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
        IdentifierRegex().IsMatch(name) ? $"\"{name}\"" : throw new ArgumentException($"Identificador SQL inválido: '{name}'.");

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

/// <summary>Traduce <see cref="VectorFilter"/> a SQL sobre la columna <c>metadata</c> (jsonb), con parámetros posicionales.</summary>
public static class PgVectorFilterTranslator
{
    /// <summary>Devuelve la condición SQL y añade sus valores a <paramref name="parameters"/> (los índices continúan la numeración).</summary>
    public static string Translate(VectorFilter filter, List<object> parameters)
    {
        string Param(object value)
        {
            parameters.Add(value);
            return "$" + parameters.Count;
        }

        return filter switch
        {
            EqFilter eq => $"COALESCE(metadata->>{Param(eq.Key)} = {Param(eq.Value)}, false)",
            InFilter @in => $"COALESCE(metadata->>{Param(@in.Key)} = ANY({Param(@in.Values.ToArray())}), false)",
            ExistsFilter ex => $"jsonb_exists(metadata, {Param(ex.Key)})",
            AndFilter and => and.Filters.Count == 0 ? "TRUE" : "(" + string.Join(" AND ", and.Filters.Select(f => Translate(f, parameters))) + ")",
            OrFilter or => or.Filters.Count == 0 ? "FALSE" : "(" + string.Join(" OR ", or.Filters.Select(f => Translate(f, parameters))) + ")",
            NotFilter not => $"NOT {Translate(not.Inner, parameters)}",
            _ => throw new NotSupportedException($"Filtro no soportado: {filter.GetType().Name}"),
        };
    }
}
