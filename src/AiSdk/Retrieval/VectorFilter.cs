namespace AiSdk;

/// <summary>
/// Filtro declarativo sobre los metadatos de <see cref="VectorDocument"/>. Cada almacén lo traduce
/// a su propio lenguaje (SQL en pgvector, filtros de Qdrant, Pinecone...), así el filtrado ocurre
/// en la base de datos y no en memoria.
/// </summary>
/// <example><c>VectorFilter.Eq("tipo", "manual") &amp; VectorFilter.In("pais", "CL", "PE")</c></example>
public abstract record VectorFilter
{
    /// <summary>El metadato <paramref name="key"/> es igual a <paramref name="value"/>.</summary>
    public static VectorFilter Eq(string key, string value) => new EqFilter(key, value);

    /// <summary>El metadato <paramref name="key"/> es distinto de <paramref name="value"/> (o no existe).</summary>
    public static VectorFilter Ne(string key, string value) => new NotFilter(new EqFilter(key, value));

    /// <summary>El metadato <paramref name="key"/> es alguno de <paramref name="values"/>.</summary>
    public static VectorFilter In(string key, params string[] values) => new InFilter(key, values);

    /// <summary>El documento tiene el metadato <paramref name="key"/>.</summary>
    public static VectorFilter Exists(string key) => new ExistsFilter(key);

    public static VectorFilter And(params VectorFilter[] filters) => new AndFilter(filters);

    public static VectorFilter Or(params VectorFilter[] filters) => new OrFilter(filters);

    public static VectorFilter Not(VectorFilter filter) => new NotFilter(filter);

    public static VectorFilter operator &(VectorFilter a, VectorFilter b) => new AndFilter([a, b]);

    public static VectorFilter operator |(VectorFilter a, VectorFilter b) => new OrFilter([a, b]);

    public static VectorFilter operator !(VectorFilter a) => new NotFilter(a);

    /// <summary>Evalúa el filtro en memoria.</summary>
    public abstract bool Matches(IReadOnlyDictionary<string, string>? metadata);

    /// <summary>Claves de metadatos que usa el filtro.</summary>
    public abstract IEnumerable<string> Keys { get; }
}

public sealed record EqFilter(string Key, string Value) : VectorFilter
{
    public override bool Matches(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(Key, out var v) && v == Value;

    public override IEnumerable<string> Keys => [Key];
}

public sealed record InFilter(string Key, IReadOnlyList<string> Values) : VectorFilter
{
    public override bool Matches(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(Key, out var v) && Values.Contains(v);

    public override IEnumerable<string> Keys => [Key];
}

public sealed record ExistsFilter(string Key) : VectorFilter
{
    public override bool Matches(IReadOnlyDictionary<string, string>? metadata) => metadata?.ContainsKey(Key) == true;

    public override IEnumerable<string> Keys => [Key];
}

public sealed record AndFilter(IReadOnlyList<VectorFilter> Filters) : VectorFilter
{
    public override bool Matches(IReadOnlyDictionary<string, string>? metadata) => Filters.All(f => f.Matches(metadata));

    public override IEnumerable<string> Keys => Filters.SelectMany(f => f.Keys).Distinct();
}

public sealed record OrFilter(IReadOnlyList<VectorFilter> Filters) : VectorFilter
{
    public override bool Matches(IReadOnlyDictionary<string, string>? metadata) => Filters.Any(f => f.Matches(metadata));

    public override IEnumerable<string> Keys => Filters.SelectMany(f => f.Keys).Distinct();
}

public sealed record NotFilter(VectorFilter Inner) : VectorFilter
{
    public override bool Matches(IReadOnlyDictionary<string, string>? metadata) => !Inner.Matches(metadata);

    public override IEnumerable<string> Keys => Inner.Keys;
}
