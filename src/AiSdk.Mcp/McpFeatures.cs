using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiSdk.Mcp;

/// <summary>Raíz (directorio o URI) que el cliente expone al servidor.</summary>
public sealed record McpRoot(string Uri, string? Name = null);

/// <summary>Recurso publicado por un servidor MCP.</summary>
public sealed record McpResource(string Uri, string Name)
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? MimeType { get; init; }
}

/// <summary>Plantilla de recurso (URI con parámetros, p. ej. <c>file:///{path}</c>).</summary>
public sealed record McpResourceTemplate(string UriTemplate, string Name)
{
    public string? Description { get; init; }

    public string? MimeType { get; init; }
}

/// <summary>Contenido de un recurso leído (texto o binario).</summary>
public sealed record McpResourceContent(string Uri, string? MimeType, string? Text, byte[]? Blob);

public sealed record McpPromptArgument(string Name, string? Description, bool Required);

/// <summary>Prompt publicado por un servidor MCP.</summary>
public sealed record McpPrompt(string Name, string? Description, IReadOnlyList<McpPromptArgument> Arguments)
{
    public string? Title { get; init; }
}

/// <summary>Prompt resuelto, listo para usar como mensajes del modelo.</summary>
public sealed record McpPromptResult(string? Description, IReadOnlyList<ModelMessage> Messages);

/// <summary>Petición de <c>sampling/createMessage</c>: el servidor pide al cliente que genere con un modelo.</summary>
public sealed record McpSamplingRequest(
    IReadOnlyList<ModelMessage> Messages,
    string? SystemPrompt,
    int? MaxTokens,
    double? Temperature,
    IReadOnlyList<string>? StopSequences,
    JsonNode? ModelPreferences);

public sealed record McpSamplingResult(string Text, string Model, string StopReason = "endTurn");

/// <summary>Petición de <c>elicitation/create</c>: el servidor pide datos al usuario.</summary>
public sealed record McpElicitationRequest(string Message, JsonElement RequestedSchema);

/// <summary>Respuesta a una elicitación: <c>accept</c> (con datos), <c>decline</c> o <c>cancel</c>.</summary>
public sealed record McpElicitationResult(string Action, JsonObject? Content = null)
{
    public static McpElicitationResult Accept(JsonObject content) => new("accept", content);

    public static McpElicitationResult Decline() => new("decline");

    public static McpElicitationResult Cancel() => new("cancel");
}

internal static class McpConvert
{
    public static ContentPart? ToContentPart(JsonObject content) => (string?)content["type"] switch
    {
        "text" => new TextPart((string?)content["text"] ?? string.Empty),
        "image" => new ImagePart { Data = Convert.FromBase64String((string?)content["data"] ?? string.Empty), MediaType = (string?)content["mimeType"] ?? "image/png" },
        "audio" => new FilePart { Data = Convert.FromBase64String((string?)content["data"] ?? string.Empty), MediaType = (string?)content["mimeType"] ?? "audio/wav" },
        "resource" when content["resource"] is JsonObject r && (string?)r["text"] is { } text => new TextPart(text),
        "resource" when content["resource"] is JsonObject r && (string?)r["blob"] is { } blob => new FilePart
        {
            Data = Convert.FromBase64String(blob),
            MediaType = (string?)r["mimeType"] ?? "application/octet-stream",
            Filename = (string?)r["uri"],
        },
        "resource_link" => new TextPart($"[{(string?)content["name"]}]({(string?)content["uri"]})"),
        _ => null,
    };

    public static ModelMessage ToModelMessage(JsonObject message)
    {
        var role = (string?)message["role"] == "assistant" ? ChatRole.Assistant : ChatRole.User;
        var parts = message["content"] switch
        {
            JsonObject single => [ToContentPart(single)],
            JsonArray many => many.OfType<JsonObject>().Select(ToContentPart).ToList(),
            _ => [],
        };
        return new ModelMessage(role, parts.OfType<ContentPart>().ToList());
    }
}
