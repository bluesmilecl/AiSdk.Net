using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Internal;
using AiSdk.Resources;

namespace AiSdk;

/// <summary>
/// Structured output specification (equivalent to the AI SDK's <c>Output.object()</c>,
/// <c>Output.array()</c> and <c>Output.choice()</c>).
/// </summary>
public abstract class Output
{
    public abstract ResponseFormat ResponseFormat { get; }

    public abstract Type OutputType { get; }

    public abstract object? ParseUntyped(string text);

    public abstract object? ParsePartialUntyped(string text);

    public static Output<string> Text() => new TextOutput();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mismo nombre que en el AI SDK (Output.object / result.object) para facilitar la migración.")]
    public static Output<T> Object<T>(string? name = null, string? description = null, JsonSerializerOptions? options = null) =>
        new ObjectOutput<T>(name, description, options ?? JsonSchema.DefaultSerializerOptions);

    /// <summary>Array of elements. Internally it is wrapped in <c>{ "elements": [...] }</c>.</summary>
    public static Output<IReadOnlyList<T>> Array<T>(string? name = null, string? description = null, JsonSerializerOptions? options = null) =>
        new ArrayOutput<T>(name, description, options ?? JsonSchema.DefaultSerializerOptions);

    /// <summary>Classification: the model chooses one of the given values.</summary>
    public static Output<string> Choice(params string[] options) => new ChoiceOutput(options);
}

public abstract class Output<T> : Output
{
    public override Type OutputType => typeof(T);

    public abstract T Parse(string text);

    public abstract T? ParsePartial(string text);

    public override object? ParseUntyped(string text) => Parse(text);

    public override object? ParsePartialUntyped(string text) => ParsePartial(text);

    internal static NoObjectGeneratedException Fail(string text, Exception? inner = null) =>
        new(Strings.Output_ParseFailed, text, inner);
}

internal sealed class TextOutput : Output<string>
{
    public override ResponseFormat ResponseFormat => ResponseFormat.Text;

    public override string Parse(string text) => text;

    public override string ParsePartial(string text) => text;
}

internal sealed class ObjectOutput<T>(string? name, string? description, JsonSerializerOptions options) : Output<T>
{
    private readonly JsonSerializerOptions _partialOptions = JsonSchema.Lenient(options);
    private readonly ResponseFormat _format = ResponseFormat.Json(JsonSchema.For<T>(options), name ?? typeof(T).Name, description);

    public override ResponseFormat ResponseFormat => _format;

    public override T Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(StripFences(text), options) ?? throw Fail(text);
        }
        catch (JsonException ex)
        {
            throw Fail(text, ex);
        }
    }

    public override T? ParsePartial(string text)
    {
        var node = JsonUtil.ParsePartial(text);
        if (node is null)
        {
            return default;
        }

        try
        {
            return node.Deserialize<T>(_partialOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static string StripFences(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = t.IndexOf('\n');
            var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine > 0 && lastFence > firstNewLine)
            {
                t = t[(firstNewLine + 1)..lastFence];
            }
        }

        return t;
    }
}

internal sealed class ArrayOutput<T>(string? name, string? description, JsonSerializerOptions options) : Output<IReadOnlyList<T>>
{
    private readonly JsonSerializerOptions _partialOptions = JsonSchema.Lenient(options);

    private readonly ResponseFormat _format = ResponseFormat.Json(
        JsonSchema.Object([("elements", new JsonObject { ["type"] = "array", ["items"] = JsonSchema.NodeFor(typeof(T), options) }, true)]),
        name ?? typeof(T).Name + "List",
        description);

    public override ResponseFormat ResponseFormat => _format;

    public override IReadOnlyList<T> Parse(string text)
    {
        try
        {
            var node = JsonNode.Parse(ObjectOutput<T>.StripFences(text));
            var elements = node?["elements"] ?? throw Fail(text);
            return elements.Deserialize<List<T>>(options) ?? throw Fail(text);
        }
        catch (JsonException ex)
        {
            throw Fail(text, ex);
        }
    }

    public override IReadOnlyList<T>? ParsePartial(string text)
    {
        if (JsonUtil.ParsePartial(text)?["elements"] is not JsonArray arr)
        {
            return null;
        }

        var list = new List<T>();
        // El último elemento puede estar incompleto: solo se emiten los que ya están cerrados.
        foreach (var item in arr.Take(Math.Max(0, arr.Count - 1)))
        {
            try
            {
                if (item.Deserialize<T>(_partialOptions) is { } value)
                {
                    list.Add(value);
                }
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }
}

internal sealed class ChoiceOutput(string[] choices) : Output<string>
{
    private readonly ResponseFormat _format = ResponseFormat.Json(
        JsonSchema.Object([("result", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(choices.Select(c => (JsonNode?)c).ToArray()) }, true)]),
        "choice");

    public override ResponseFormat ResponseFormat => _format;

    public override string Parse(string text)
    {
        try
        {
            var value = JsonNode.Parse(ObjectOutput<string>.StripFences(text))?["result"]?.GetValue<string>();
            return value is not null && choices.Contains(value) ? value : throw Fail(text);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw Fail(text, ex);
        }
    }

    public override string? ParsePartial(string text)
    {
        var value = JsonUtil.ParsePartial(text)?["result"]?.GetValue<string>();
        return value is null ? null : choices.FirstOrDefault(c => c.StartsWith(value, StringComparison.Ordinal)) is { } match && match == value ? match : null;
    }
}
