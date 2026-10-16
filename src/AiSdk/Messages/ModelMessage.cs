using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSdk;

/// <summary>Rol de un mensaje dentro de la conversación.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatRole>))]
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// Mensaje independiente del proveedor (equivalente a <c>ModelMessage</c> del AI SDK).
/// Es serializable a JSON, por lo que puede persistirse tal cual en una base de datos.
/// </summary>
public sealed record ModelMessage
{
    [JsonConstructor]
    public ModelMessage(ChatRole role, IReadOnlyList<ContentPart> content)
    {
        Role = role;
        Content = content;
    }

    public ChatRole Role { get; init; }

    public IReadOnlyList<ContentPart> Content { get; init; }

    /// <summary>Texto concatenado de todas las partes <see cref="TextPart"/>.</summary>
    [JsonIgnore]
    public string Text => string.Concat(Content.OfType<TextPart>().Select(p => p.Text));

    public static ModelMessage System(string text) => new(ChatRole.System, [new TextPart(text)]);

    public static ModelMessage User(string text) => new(ChatRole.User, [new TextPart(text)]);

    public static ModelMessage User(params ContentPart[] parts) => new(ChatRole.User, parts);

    public static ModelMessage Assistant(string text) => new(ChatRole.Assistant, [new TextPart(text)]);

    public static ModelMessage Assistant(params ContentPart[] parts) => new(ChatRole.Assistant, parts);

    public static ModelMessage Tool(params ToolResultPart[] results) => new(ChatRole.Tool, results);
}

/// <summary>Parte de contenido de un mensaje.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ReasoningPart), "reasoning")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(FilePart), "file")]
[JsonDerivedType(typeof(ToolCallPart), "tool-call")]
[JsonDerivedType(typeof(ToolResultPart), "tool-result")]
[JsonDerivedType(typeof(SourcePart), "source")]
[JsonDerivedType(typeof(RawProviderPart), "raw")]
public abstract record ContentPart
{
    /// <summary>
    /// Metadatos específicos del proveedor que deben conservarse entre turnos
    /// (por ejemplo la firma de un bloque de razonamiento de Anthropic).
    /// Las claves usan el formato <c>"proveedor.campo"</c>.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ProviderMetadata { get; init; }
}

public sealed record TextPart(string Text) : ContentPart;

/// <summary>Razonamiento ("thinking") emitido por el modelo.</summary>
public sealed record ReasoningPart(string Text) : ContentPart;

/// <summary>Imagen, ya sea en bytes o mediante URL.</summary>
public sealed record ImagePart : ContentPart
{
    public byte[]? Data { get; init; }

    public Uri? Url { get; init; }

    public string MediaType { get; init; } = "image/png";

    public static ImagePart FromBytes(byte[] data, string mediaType = "image/png") => new() { Data = data, MediaType = mediaType };

    public static ImagePart FromUrl(string url, string mediaType = "image/png") => new() { Url = new Uri(url), MediaType = mediaType };
}

/// <summary>Archivo (PDF, texto, audio...).</summary>
public sealed record FilePart : ContentPart
{
    public byte[]? Data { get; init; }

    public Uri? Url { get; init; }

    public required string MediaType { get; init; }

    public string? Filename { get; init; }
}

/// <summary>Llamada a una herramienta solicitada por el modelo.</summary>
public sealed record ToolCallPart(string ToolCallId, string ToolName, JsonElement Input) : ContentPart
{
    /// <summary>True si el proveedor no pudo interpretar el JSON de entrada (p. ej. salida truncada).</summary>
    public bool InvalidInput { get; init; }

    /// <summary>True si la herramienta la ejecutó el proveedor (búsqueda web, ejecución de código...).</summary>
    public bool ProviderExecuted { get; init; }
}

/// <summary>Resultado de la ejecución de una herramienta.</summary>
public sealed record ToolResultPart(string ToolCallId, string ToolName, JsonElement Output) : ContentPart
{
    public bool IsError { get; init; }

    /// <summary>True si el resultado lo produjo el proveedor (herramienta de servidor).</summary>
    public bool ProviderExecuted { get; init; }
}

/// <summary>Fuente o cita usada por el modelo (búsqueda web, documentos citados...).</summary>
public sealed record SourcePart(string Id, string Url) : ContentPart
{
    public string? Title { get; init; }

    /// <summary>Texto citado de la fuente, si el proveedor lo indica.</summary>
    public string? CitedText { get; init; }
}

/// <summary>
/// Bloque específico de un proveedor que el SDK no interpreta pero que debe
/// conservarse y reenviarse tal cual al mismo proveedor en los turnos siguientes.
/// </summary>
public sealed record RawProviderPart(string Provider, JsonElement Data) : ContentPart;
