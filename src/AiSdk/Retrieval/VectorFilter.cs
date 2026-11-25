namespace AiSdk;

/// <summary>
/// Declarative filter over the metadata of <see cref="VectorDocument"/>. Each store translates it
/// into its own language (SQL in pgvector, Qdrant filters, Pinecone...), so filtering happens
/// in the database rather than in memory.
/// </summary>
/// <example><c>VectorFilter.Eq("type", "manual") &amp; VectorFilter.In("country", "CL", "PE")</c></example>
public abstract record VectorFilter
{
    /// <summary>The metadata entry <paramref name="key"/> equals <paramref name="value"/>.</summary>
    public static VectorFilter Eq(string key, string value) => new EqFilter(key, value);

    /// <summary>
    /// The metadata entry <paramref name="key"/> differs from <paramref name="value"/> (or does not exist).
    /// </summary>
    public static VectorFilter Ne(string key, string value) => new NotFilter(new EqFilter(key, value));

    /// <summary>The metadata entry <paramref name="key"/> is one of <paramref name="values"/>.</summary>
    public static VectorFilter In(string key, params string[] values) => new InFilter(key, values);

    /// <summary>The document has the metadata entry <paramref name="key"/>.</summary>
    public static VectorFilter Exists(string key) => new ExistsFilter(key);

    public static VectorFilter And(params VectorFilter[] filters) => new AndFilter(filters);

    public static VectorFilter Or(params VectorFilter[] filters) => new OrFilter(filters);

    public static VectorFilter Not(VectorFilter filter) => new NotFilter(filter);

    public static VectorFilter operator &(VectorFilter a, VectorFilter b) => new AndFilter([a, b]);

    public static VectorFilter operator |(VectorFilter a, VectorFilter b) => new OrFilter([a, b]);

    public static VectorFilter operator !(VectorFilter a) => new NotFilter(a);

    /// <summary>Evaluates the filter in memory.</summary>
    public abstract bool Matches(IReadOnlyDictionary<string, string>? metadata);

    /// <summary>Gets the metadata keys used by the filter.</summary>
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
