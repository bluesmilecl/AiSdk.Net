using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AiSdk;

/// <summary>
/// Generación de JSON Schema a partir de tipos .NET (equivalente a usar Zod en el AI SDK).
/// Usa <see cref="JsonSchemaExporter"/> de System.Text.Json y respeta
/// <see cref="DescriptionAttribute"/> en tipos, propiedades y parámetros.
/// </summary>
public static class JsonSchema
{
    /// <summary>Opciones de serialización por defecto: camelCase, enums como texto.</summary>
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

    /// <summary>Versión tolerante de unas opciones (para deserializar objetos parciales durante el streaming).</summary>
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

    /// <summary>Genera el esquema de <typeparamref name="T"/>.</summary>
    public static JsonElement For<T>(JsonSerializerOptions? options = null) => For(typeof(T), options);

    /// <summary>Genera el esquema de <paramref name="type"/>.</summary>
    public static JsonElement For(Type type, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToElement(NodeFor(type, options));

    /// <summary>Genera el esquema como <see cref="JsonNode"/> (editable).</summary>
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
    /// Crea un esquema de objeto a partir de un conjunto de propiedades (útil para herramientas definidas a mano).
    /// </summary>
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

    /// <summary>Parsea un esquema escrito a mano en JSON.</summary>
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Convierte un esquema al modo "estricto" que exigen OpenAI y Anthropic para
    /// salidas estructuradas: <c>additionalProperties: false</c> y todas las propiedades requeridas.
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
