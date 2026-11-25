using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSdk;

/// <summary>Role of a message within the conversation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatRole>))]
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// Provider-independent message (equivalent to the AI SDK's <c>ModelMessage</c>).
/// It is JSON-serializable, so it can be persisted as is in a database.
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

    /// <summary>Gets the concatenated text of all the <see cref="TextPart"/> parts.</summary>
    [JsonIgnore]
    public string Text => string.Concat(Content.OfType<TextPart>().Select(p => p.Text));

    public static ModelMessage System(string text) => new(ChatRole.System, [new TextPart(text)]);

    public static ModelMessage User(string text) => new(ChatRole.User, [new TextPart(text)]);

    public static ModelMessage User(params ContentPart[] parts) => new(ChatRole.User, parts);

    public static ModelMessage Assistant(string text) => new(ChatRole.Assistant, [new TextPart(text)]);

    public static ModelMessage Assistant(params ContentPart[] parts) => new(ChatRole.Assistant, parts);

    public static ModelMessage Tool(params ToolResultPart[] results) => new(ChatRole.Tool, results);
}

/// <summary>Content part of a message.</summary>
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
    /// Gets the provider-specific metadata that must be preserved across turns
    /// (for example, the signature of an Anthropic reasoning block).
    /// Keys use the <c>"provider.field"</c> format.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ProviderMetadata { get; init; }
}

public sealed record TextPart(string Text) : ContentPart;

/// <summary>Reasoning ("thinking") emitted by the model.</summary>
public sealed record ReasoningPart(string Text) : ContentPart;

/// <summary>Image, either as bytes or through a URL.</summary>
public sealed record ImagePart : ContentPart
{
    public byte[]? Data { get; init; }

    public Uri? Url { get; init; }

    public string MediaType { get; init; } = "image/png";

    /// <summary>Gets the id of a file already uploaded to the provider (Anthropic or OpenAI Files API).</summary>
    public string? FileId { get; init; }

    public static ImagePart FromFileId(string fileId, string mediaType = "image/png") => new() { FileId = fileId, MediaType = mediaType };

    public static ImagePart FromBytes(byte[] data, string mediaType = "image/png") => new() { Data = data, MediaType = mediaType };

    public static ImagePart FromUrl(string url, string mediaType = "image/png") => new() { Url = new Uri(url), MediaType = mediaType };
}

/// <summary>File (PDF, text, audio...).</summary>
public sealed record FilePart : ContentPart
{
    public byte[]? Data { get; init; }

    public Uri? Url { get; init; }

    public required string MediaType { get; init; }

    public string? Filename { get; init; }

    /// <summary>Gets the id of a file already uploaded to the provider (Anthropic or OpenAI Files API).</summary>
    public string? FileId { get; init; }

    public static FilePart FromFileId(string fileId, string mediaType = "application/pdf", string? filename = null) =>
        new() { FileId = fileId, MediaType = mediaType, Filename = filename };
}

/// <summary>Tool call requested by the model.</summary>
public sealed record ToolCallPart(string ToolCallId, string ToolName, JsonElement Input) : ContentPart
{
    /// <summary>True if the provider could not parse the input JSON (e.g. truncated output).</summary>
    public bool InvalidInput { get; init; }

    /// <summary>True if the tool was executed by the provider (web search, code execution...).</summary>
    public bool ProviderExecuted { get; init; }
}

/// <summary>Result of a tool execution.</summary>
public sealed record ToolResultPart(string ToolCallId, string ToolName, JsonElement Output) : ContentPart
{
    public bool IsError { get; init; }

    /// <summary>True if the result was produced by the provider (server-side tool).</summary>
    public bool ProviderExecuted { get; init; }
}

/// <summary>Source or citation used by the model (web search, cited documents...).</summary>
public sealed record SourcePart(string Id, string Url) : ContentPart
{
    public string? Title { get; init; }

    /// <summary>Gets the text cited from the source, if the provider reports it.</summary>
    public string? CitedText { get; init; }
}

/// <summary>
/// Provider-specific block that the SDK does not interpret but that must be
/// preserved and sent back as is to the same provider in subsequent turns.
/// </summary>
public sealed record RawProviderPart(string Provider, JsonElement Data) : ContentPart;
