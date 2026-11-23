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

/// <summary>Cómo se resuelve una búsqueda semántica con <see cref="VectorFilter"/>.</summary>
/// <remarks>
/// El índice HNSW entrega a lo más <c>hnsw.ef_search</c> candidatos (40 por defecto) y el filtro se aplica después:
/// un filtro selectivo (un usuario, un cliente) puede dejar la búsqueda con menos resultados de los pedidos, o sin
/// ninguno, aunque haya filas que lo cumplan.
/// </remarks>
public enum PgVectorFilteredSearch
{
    /// <summary>
    /// Índice HNSW y, si el filtro deja menos de <c>topK</c> resultados (o menos de <c>hnsw.ef_search</c> cuando
    /// <c>topK</c> es mayor), búsqueda exacta sobre las filas que cumplen el filtro, que se ubican con el índice GIN de
    /// metadatos. Es una sola consulta: la parte exacta no se ejecuta si no hace falta. Su costo crece con las filas que
    /// cumplen la parte indexable del filtro (igualdades e <c>In</c>; no <c>Not</c> ni <c>Exists</c>): en tablas muy
    /// grandes con filtros poco selectivos en esa parte, considere <see cref="Approximate"/> y subir
    /// <c>hnsw.ef_search</c>.
    /// </summary>
    Auto,

    /// <summary>Solo el índice HNSW con el filtro aplicado después (lo más rápido; puede devolver menos resultados).</summary>
    Approximate,

    /// <summary>
    /// Siempre búsqueda exacta sobre las filas que cumplen el filtro (resultados completos; el costo crece con la
    /// cantidad de filas que lo cumplen). Sin filtro se usa el índice HNSW.
    /// </summary>
    Exact,
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

    /// <summary>Estrategia de las búsquedas semánticas con filtro (también la parte semántica de la híbrida).</summary>
    public PgVectorFilteredSearch FilteredSearch { get; init; } = PgVectorFilteredSearch.Auto;
}

/// <summary>
/// Almacén vectorial sobre PostgreSQL + pgvector. Guarda texto, metadatos (jsonb) y embedding,
/// filtra en SQL y ofrece búsqueda híbrida con Reciprocal Rank Fusion. Los filtros de igualdad se
/// traducen a contención jsonb (<c>@&gt;</c>), que usa el índice GIN de metadatos; ver
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
    /// CTEs que terminan en <c>hits(id, distance)</c>: los <paramref name="limit"/> documentos más cercanos a
    /// <c>$1</c> que cumplen el filtro, según <see cref="PgVectorStoreOptions.FilteredSearch"/>.
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

    /// <summary>Puntuación (más alto = más parecido) a partir de la distancia del operador de <see cref="PgVectorStoreOptions.Distance"/>.</summary>
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

/// <summary>
/// Traduce <see cref="VectorFilter"/> a SQL sobre la columna <c>metadata</c> (jsonb), con parámetros posicionales.
/// </summary>
/// <remarks>
/// Las igualdades se escriben como contención jsonb (<c>metadata @&gt; '{"clave":"valor"}'</c>), que usa el índice GIN
/// <c>jsonb_path_ops</c> que crea <see cref="PgVectorStore"/>; las de un mismo <c>And</c> se combinan en un solo objeto.
/// <see cref="VectorFilter.In"/> usa <c>@&gt; ANY(...)</c> (también indexable) y <see cref="VectorFilter.Exists"/> el
/// operador <c>?</c> (indexable con un GIN <c>jsonb_ops</c>). Los metadatos se comparan como texto JSON: es lo que guarda
/// <see cref="PgVectorStore.UpsertAsync"/>; un valor numérico o booleano escrito por otra herramienta no coincide con
/// <see cref="VectorFilter.Eq"/>.
/// </remarks>
public static class PgVectorFilterTranslator
{
    /// <summary>Devuelve la condición SQL y añade sus valores a <paramref name="parameters"/> (los índices continúan la numeración).</summary>
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
        _ => throw new NotSupportedException($"Filtro no soportado: {filter.GetType().Name}"),
    };

    /// <summary>Las igualdades del And (también las anidadas) van en un solo <c>@&gt;</c>: una búsqueda en el índice.</summary>
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
