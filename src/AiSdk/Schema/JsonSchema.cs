using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AiSdk;

/// <summary>
/// JSON Schema generation from .NET types (equivalent to using Zod in the AI SDK).
/// Uses System.Text.Json's <see cref="JsonSchemaExporter"/> and honors
/// <see cref="DescriptionAttribute"/> on types, properties and parameters.
/// </summary>
public static class JsonSchema
{
    /// <summary>Gets the default serialization options: camelCase, enums as strings.</summary>
    public static JsonSerializerOptions DefaultSerializerOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            // Conserva acentos y símbolos (°, ñ...) sin escapar: menos tokens para el modelo.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Lenient version of a set of options (for deserializing partial objects during streaming).</summary>
    internal static JsonSerializerOptions Lenient(JsonSerializerOptions options)
    {
        var lenient = new JsonSerializerOptions(options)
        {
            RespectNullableAnnotations = false,
            RespectRequiredConstructorParameters = false,
        };
        lenient.MakeReadOnly();
        return lenient;
    }

    /// <summary>Generates the schema of <typeparamref name="T"/>.</summary>
    public static JsonElement For<T>(JsonSerializerOptions? options = null) => For(typeof(T), options);

    /// <summary>Generates the schema of <paramref name="type"/>.</summary>
    public static JsonElement For(Type type, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToElement(NodeFor(type, options));

    /// <summary>Generates the schema as a <see cref="JsonNode"/> (editable).</summary>
    public static JsonNode NodeFor(Type type, JsonSerializerOptions? options = null)
    {
        options ??= DefaultSerializerOptions;
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = AddDescriptions,
        };
        var node = options.GetJsonSchemaAsNode(type, exporterOptions);
        if (node is JsonObject obj)
        {
            obj.Remove("$schema");
        }

        return node;
    }

    /// <summary>
    /// Creates an object schema from a set of properties (useful for hand-written tools).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Nombre del tipo JSON Schema (\"type\": \"object\").")]
    public static JsonElement Object(IEnumerable<(string Name, JsonNode Schema, bool Required)> properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in properties)
        {
            props[name] = schema;
            if (isRequired)
            {
                required.Add(name);
            }
        }

        var obj = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0)
        {
            obj["required"] = required;
        }

        return JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>Parses a schema hand-written in JSON.</summary>
    public static JsonElement Parse(string json) => JsonElement.Parse(json);

    /// <summary>
    /// Converts a schema to the "strict" mode that OpenAI and Anthropic require for
    /// structured outputs: <c>additionalProperties: false</c> and all properties required.
    /// </summary>
    public static JsonElement ToStrict(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Strictify(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Strictify(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject props)
                {
                    obj["additionalProperties"] = false;
                    obj["required"] = new JsonArray(props.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Strictify(child);
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    Strictify(item);
                }

                break;
        }
    }

    private static JsonNode AddDescriptions(JsonSchemaExporterContext context, JsonNode schema)
    {
        var description = context.PropertyInfo?.AttributeProvider?
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()?.Description;

        if (description is null && context.PropertyInfo is null)
        {
            description = context.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>()?.Description;
        }

        if (description is null)
        {
            return schema;
        }

        if (schema is not JsonObject obj)
        {
            // Los esquemas booleanos (true) se convierten a objeto para poder añadir la descripción.
            obj = new JsonObject();
        }

        obj.Insert(0, "description", description);
        return obj;
    }
}
