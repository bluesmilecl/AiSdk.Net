using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiSdk.Mcp;

/// <summary>Raíz (directorio o URI) que el cliente expone al servidor.</summary>
[Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
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
[Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
public sealed record McpSamplingRequest(
    IReadOnlyList<ModelMessage> Messages,
    string? SystemPrompt,
    int? MaxTokens,
    double? Temperature,
    IReadOnlyList<string>? StopSequences,
    JsonNode? ModelPreferences);

[Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
public sealed record McpSamplingResult(string Text, string Model, string StopReason = "endTurn");

/// <summary>
/// Lánzala desde <see cref="McpClientOptions.SamplingHandler"/> o <see cref="McpClientOptions.ElicitationHandler"/>
/// para rechazar una petición del servidor: el cliente responde con el código <c>-1</c> ("rechazada por el usuario").
/// </summary>
public sealed class McpRequestRejectedException(string message = "El usuario rechazó la petición.") : McpException(message, -1);

/// <summary>Qué hacer cuando la definición de una herramienta MCP cambia respecto de la fijada (posible "rug pull").</summary>
public enum McpToolChangePolicy
{
    /// <summary>
    /// La herramienta queda bloqueada en este cliente: <see cref="McpClient.GetToolsAsync"/> la omite y
    /// <see cref="McpClient.CallToolAsync"/> rechaza las llamadas. Es el valor por defecto.
    /// </summary>
    Reject,

    /// <summary>Se acepta la nueva definición y se fija en su lugar (solo se notifica con <see cref="McpClient.ToolDefinitionChanged"/>).</summary>
    Accept,
}

/// <summary>Cambio detectado en la definición de una herramienta MCP ya fijada.</summary>
/// <param name="Name">Nombre de la herramienta en el servidor.</param>
/// <param name="PinnedHash">Hash fijado la primera vez que se listó (o el de <see cref="McpClientOptions.PinnedToolHashes"/>).</param>
/// <param name="NewHash">Hash de la definición recién listada.</param>
/// <param name="Tool">Definición nueva.</param>
public sealed record McpToolDefinitionChange(string Name, string PinnedHash, string NewHash, McpToolInfo Tool);

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

/// <summary>Identificadores de diagnóstico del cliente MCP.</summary>
public static class McpDiagnostics
{
    /// <summary>
    /// Funciones que la especificación MCP 2026-07-28 deja obsoletas (roots, sampling y logging; se mantienen al menos
    /// 12 meses): el SDK las conserva para servidores 2025-11-25, pero pueden cambiar o desaparecer antes de la 1.0.
    /// Para usarlas, suprima el diagnóstico (<c>&lt;NoWarn&gt;$(NoWarn);AISDKMCP001&lt;/NoWarn&gt;</c>).
    /// </summary>
    public const string DeprecatedInSpec = "AISDKMCP001";

    internal const string UrlFormat = "https://github.com/bluesmilecl/AiSdk.Net/wiki/MCP#{0}";
}
