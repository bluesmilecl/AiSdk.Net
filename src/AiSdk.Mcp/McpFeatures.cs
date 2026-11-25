using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiSdk.Mcp;

/// <summary>Root (directory or URI) that the client exposes to the server.</summary>
[Experimental(McpDiagnostics.DeprecatedInSpec, UrlFormat = McpDiagnostics.UrlFormat)]
public sealed record McpRoot(string Uri, string? Name = null);

/// <summary>Resource published by an MCP server.</summary>
public sealed record McpResource(string Uri, string Name)
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? MimeType { get; init; }
}

/// <summary>Resource template (URI with parameters, e.g. <c>file:///{path}</c>).</summary>
public sealed record McpResourceTemplate(string UriTemplate, string Name)
{
    public string? Description { get; init; }

    public string? MimeType { get; init; }
}

/// <summary>Contents of a read resource (text or binary).</summary>
public sealed record McpResourceContent(string Uri, string? MimeType, string? Text, byte[]? Blob);

public sealed record McpPromptArgument(string Name, string? Description, bool Required);

/// <summary>Prompt published by an MCP server.</summary>
public sealed record McpPrompt(string Name, string? Description, IReadOnlyList<McpPromptArgument> Arguments)
{
    public string? Title { get; init; }
}

/// <summary>Resolved prompt, ready to use as model messages.</summary>
public sealed record McpPromptResult(string? Description, IReadOnlyList<ModelMessage> Messages);

/// <summary><c>sampling/createMessage</c> request: the server asks the client to generate with a model.</summary>
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
/// Throw it from <see cref="McpClientOptions.SamplingHandler"/> or <see cref="McpClientOptions.ElicitationHandler"/>
/// to reject a server request: the client responds with code <c>-1</c> ("rejected by the user").
/// </summary>
public sealed class McpRequestRejectedException(string message = "El usuario rechazó la petición.") : McpException(message, -1);

/// <summary>What to do when an MCP tool's definition changes from the pinned one (possible "rug pull").</summary>
public enum McpToolChangePolicy
{
    /// <summary>
    /// The tool is blocked in this client: <see cref="McpClient.GetToolsAsync"/> omits it and
    /// <see cref="McpClient.CallToolAsync"/> rejects calls to it. This is the default.
    /// </summary>
    Reject,

    /// <summary>The new definition is accepted and pinned in its place (only notified via <see cref="McpClient.ToolDefinitionChanged"/>).</summary>
    Accept,
}

/// <summary>Change detected in the definition of an already pinned MCP tool.</summary>
/// <param name="Name">Name of the tool on the server.</param>
/// <param name="PinnedHash">Hash pinned the first time it was listed (or the one from <see cref="McpClientOptions.PinnedToolHashes"/>).</param>
/// <param name="NewHash">Hash of the newly listed definition.</param>
/// <param name="Tool">New definition.</param>
public sealed record McpToolDefinitionChange(string Name, string PinnedHash, string NewHash, McpToolInfo Tool);

/// <summary><c>elicitation/create</c> request: the server asks the user for data.</summary>
public sealed record McpElicitationRequest(string Message, JsonElement RequestedSchema);

/// <summary>Response to an elicitation: <c>accept</c> (with data), <c>decline</c> or <c>cancel</c>.</summary>
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

/// <summary>Diagnostic identifiers of the MCP client.</summary>
public static class McpDiagnostics
{
    /// <summary>
    /// Features that the MCP 2026-07-28 specification deprecates (roots, sampling and logging; they are kept for at least
    /// 12 months): the SDK keeps them for 2025-11-25 servers, but they may change or be removed before 1.0.
    /// To use them, suppress the diagnostic (<c>&lt;NoWarn&gt;$(NoWarn);AISDKMCP001&lt;/NoWarn&gt;</c>).
    /// </summary>
    public const string DeprecatedInSpec = "AISDKMCP001";

    internal const string UrlFormat = "https://github.com/bluesmilecl/AiSdk.Net/wiki/MCP#{0}";
}
