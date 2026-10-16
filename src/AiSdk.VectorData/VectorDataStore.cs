using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using VectorSearchResultMevd = Microsoft.Extensions.VectorData.VectorSearchResult<System.Collections.Generic.Dictionary<string, object?>>;

namespace AiSdk.VectorData;

/// <summary>Opciones de <see cref="VectorDataStore"/>.</summary>
public sealed class VectorDataStoreOptions
{
    /// <summary>Nombre de la colección (tabla, índice, colección de Qdrant...).</summary>
    public required string CollectionName { get; init; }

    /// <summary>Dimensiones de los embeddings.</summary>
    public required int Dimensions { get; init; }

    /// <summary>
    /// Metadatos que se guardan como campos indexados para poder filtrar en la base de datos.
    /// Solo estas claves pueden usarse en <see cref="VectorFilter"/>; el resto de metadatos se guarda igual
    /// pero sin indexar.
    /// </summary>
    public IReadOnlyList<string> FilterableFields { get; init; } = [];

    /// <summary>Función de distancia (por defecto coseno).</summary>
    public string DistanceFunction { get; init; } = Microsoft.Extensions.VectorData.DistanceFunction.CosineSimilarity;

    /// <summary>Tipo de índice (por defecto HNSW). Algunos conectores solo admiten ciertos tipos.</summary>
    public string? IndexKind { get; init; } = Microsoft.Extensions.VectorData.IndexKind.Hnsw;

    /// <summary>Indexa el texto para búsqueda híbrida en los conectores que la soportan.</summary>
    public bool FullTextSearch { get; init; } = true;

    /// <summary>Crea la colección si no existe en el primer uso.</summary>
    public bool CreateIfNotExists { get; init; } = true;
}

/// <summary>
/// <see cref="IVectorStore"/> sobre cualquier <see cref="VectorStore"/> de Microsoft.Extensions.VectorData.
/// Con un solo adaptador, AiSdk funciona con todos los conectores de ese ecosistema.
/// </summary>
/// <example>
/// <code>
/// var qdrant = new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true);
/// var store = new VectorDataStore(qdrant, embeddings, new() { CollectionName = "docs", Dimensions = 1536, FilterableFields = ["tipo"] });
/// </code>
/// </example>
public sealed class VectorDataStore : IHybridVectorStore
{
    public const string KeyField = "Id";
    public const string TextField = "Text";
    public const string MetadataField = "MetadataJson";
    public const string EmbeddingField = "Embedding";

    private static readonly MethodInfo IndexerGetter = typeof(Dictionary<string, object?>).GetProperty("Item")!.GetGetMethod()!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(string));

    private readonly VectorStoreCollection<object, Dictionary<string, object?>> _collection;
    private readonly IEmbeddingModel _embeddings;
    private readonly VectorDataStoreOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public VectorDataStore(VectorStore vectorStore, IEmbeddingModel embeddings, VectorDataStoreOptions options)
    {
        _embeddings = embeddings;
        _options = options;
        foreach (var field in options.FilterableFields)
        {
            if (field is KeyField or TextField or MetadataField or EmbeddingField)
            {
                throw new ArgumentException($"'{field}' es un nombre reservado.", nameof(options));
            }
        }

        _collection = vectorStore.GetDynamicCollection(options.CollectionName, BuildDefinition(options));
    }

    /// <summary>Colección subyacente de Microsoft.Extensions.VectorData (para operaciones avanzadas).</summary>
    public VectorStoreCollection<object, Dictionary<string, object?>> Collection => _collection;

    /// <summary>Definición de la colección que usa este adaptador.</summary>
    public static VectorStoreCollectionDefinition BuildDefinition(VectorDataStoreOptions options)
    {
        var properties = new List<VectorStoreProperty>
        {
            new VectorStoreKeyProperty(KeyField, typeof(string)),
            new VectorStoreDataProperty(TextField, typeof(string)) { IsFullTextIndexed = options.FullTextSearch },
            new VectorStoreDataProperty(MetadataField, typeof(string)),
        };
        properties.AddRange(options.FilterableFields.Select(f => new VectorStoreDataProperty(f, typeof(string)) { IsIndexed = true }));
        properties.Add(new VectorStoreVectorProperty(EmbeddingField, typeof(ReadOnlyMemory<float>), options.Dimensions)
        {
            DistanceFunction = options.DistanceFunction,
            IndexKind = options.IndexKind,
        });
        return new VectorStoreCollectionDefinition { Properties = properties };
    }

    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_initialized || !_options.CreateIfNotExists)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await _collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
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
        var records = new List<Dictionary<string, object?>>(list.Count);
        foreach (var doc in list)
        {
            var record = new Dictionary<string, object?>
            {
                [KeyField] = doc.Id,
                [TextField] = doc.Text,
                [MetadataField] = JsonSerializer.Serialize(doc.Metadata ?? new Dictionary<string, string>()),
                [EmbeddingField] = new ReadOnlyMemory<float>(doc.Embedding ?? computed[next++]),
            };
            foreach (var field in _options.FilterableFields)
            {
                record[field] = doc.Metadata is not null && doc.Metadata.TryGetValue(field, out var value) ? value : null;
            }

            records.Add(record);
        }

        await _collection.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var embedding = new ReadOnlyMemory<float>((await Ai.EmbedAsync(_embeddings, query, cancellationToken).ConfigureAwait(false)).Embedding);
        var options = new VectorSearchOptions<Dictionary<string, object?>> { Filter = filter is null ? null : ToExpression(filter) };
        var results = new List<VectorSearchResult>();
        await foreach (var hit in _collection.SearchAsync(embedding, topK, options, cancellationToken).ConfigureAwait(false))
        {
            results.Add(ToResult(hit));
        }

        return results;
    }

    /// <summary>
    /// Búsqueda híbrida si el conector la implementa (<see cref="IKeywordHybridSearchable{TRecord}"/>);
    /// si no, cae a la búsqueda semántica.
    /// </summary>
    public async Task<IReadOnlyList<VectorSearchResult>> HybridSearchAsync(
        string query,
        int topK = 5,
        VectorFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (_collection is not IKeywordHybridSearchable<Dictionary<string, object?>> hybrid)
        {
            return await SearchAsync(query, topK, filter, cancellationToken).ConfigureAwait(false);
        }

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var embedding = new ReadOnlyMemory<float>((await Ai.EmbedAsync(_embeddings, query, cancellationToken).ConfigureAwait(false)).Embedding);
        var keywords = query.Split([' ', ',', ';', '.', '?', '¿', '!', '¡'], StringSplitOptions.RemoveEmptyEntries);
        var options = new HybridSearchOptions<Dictionary<string, object?>> { Filter = filter is null ? null : ToExpression(filter) };
        var results = new List<VectorSearchResult>();
        await foreach (var hit in hybrid.HybridSearchAsync(embedding, keywords, topK, options, cancellationToken).ConfigureAwait(false))
        {
            results.Add(ToResult(hit));
        }

        return results;
    }

    public async Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _collection.DeleteAsync(ids.Cast<object>(), cancellationToken).ConfigureAwait(false);
    }

    private static VectorSearchResult ToResult(VectorSearchResultMevd hit)
    {
        var record = hit.Record;
        var metadata = record.TryGetValue(MetadataField, out var json) && json is string s
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(s)
            : null;
        return new VectorSearchResult(
            new VectorDocument(record[KeyField]?.ToString() ?? string.Empty, record[TextField] as string ?? string.Empty)
            {
                Metadata = metadata is { Count: > 0 } ? metadata : null,
            },
            hit.Score ?? 0);
    }

    /// <summary>
    /// Traduce el filtro a una expresión LINQ sobre el registro dinámico, que cada conector convierte
    /// a su lenguaje nativo. Solo admite campos declarados en <see cref="VectorDataStoreOptions.FilterableFields"/>.
    /// </summary>
    public Expression<Func<Dictionary<string, object?>, bool>> ToExpression(VectorFilter filter)
    {
        var record = Expression.Parameter(typeof(Dictionary<string, object?>), "r");
        return Expression.Lambda<Func<Dictionary<string, object?>, bool>>(Build(filter, record), record);
    }

    private Expression Build(VectorFilter filter, ParameterExpression record)
    {
        Expression Field(string key)
        {
            if (!_options.FilterableFields.Contains(key))
            {
                throw new NotSupportedException($"El metadato '{key}' no está en FilterableFields; declárelo para poder filtrar por él.");
            }

            return Expression.Convert(Expression.Call(record, IndexerGetter, Expression.Constant(key)), typeof(string));
        }

        return filter switch
        {
            EqFilter eq => Expression.Equal(Field(eq.Key), Expression.Constant(eq.Value, typeof(string))),
            InFilter @in => Expression.Call(EnumerableContains, Expression.Constant(@in.Values.ToArray()), Field(@in.Key)),
            AndFilter and => and.Filters.Select(f => Build(f, record)).Aggregate(Expression.AndAlso),
            OrFilter or => or.Filters.Select(f => Build(f, record)).Aggregate(Expression.OrElse),
            NotFilter not => Expression.Not(Build(not.Inner, record)),
            ExistsFilter ex => Expression.NotEqual(Field(ex.Key), Expression.Constant(null, typeof(string))),
            _ => throw new NotSupportedException($"Filtro no soportado: {filter.GetType().Name}"),
        };
    }
}
