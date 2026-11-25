using System.Text.Json;
using AiSdk.Internal;
using Microsoft.Extensions.AI;
using MEAI = Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Conversions between AiSdk content, usage and finish reasons and their Microsoft.Extensions.AI counterparts, shared
/// by both adapter directions so that wrapping one direction with the other is lossless.
/// </summary>
internal static class MeaiMapper
{
    /// <summary>
    /// Key under <see cref="AIContent.AdditionalProperties"/> that carries <see cref="ContentPart.ProviderMetadata"/> as a
    /// JSON object, so it survives a serialized chat history (where <see cref="AIContent.RawRepresentation"/> is lost).
    /// </summary>
    public const string ProviderMetadataKey = "aisdk.providerMetadata";

    /// <summary>
    /// Provider metadata key that holds the <see cref="TextReasoningContent.ProtectedData"/> of a reasoning block
    /// produced by a Microsoft.Extensions.AI client, so it can be sent back to that client unchanged.
    /// </summary>
    public const string ProtectedDataKey = "meai.protectedData";

    private static readonly JsonElement JsonNull = JsonElement.Parse("null");

    private static readonly JsonElement EmptyObjectSchema = JsonElement.Parse("""{"type":"object","properties":{}}""");

    // ---- AiSdk -> Microsoft.Extensions.AI ----

    /// <summary>
    /// Converts an AiSdk content part into an <see cref="AIContent"/>. The original part travels in
    /// <see cref="AIContent.RawRepresentation"/>; parts without a Microsoft.Extensions.AI equivalent (raw provider
    /// blocks) become an opaque <see cref="AIContent"/> carrier.
    /// </summary>
    public static AIContent ToAIContent(ContentPart part)
    {
        AIContent content = part switch
        {
            TextPart t => new TextContent(t.Text),
            ReasoningPart r => new TextReasoningContent(r.Text) { ProtectedData = EncodeProtectedData(r.ProviderMetadata) },
            ImagePart { Data: { } data } i => new DataContent(data, i.MediaType),
            ImagePart { Url: { } url } i => new UriContent(url, i.MediaType),
            ImagePart { FileId: { } fileId } i => new HostedFileContent(fileId) { MediaType = i.MediaType },
            FilePart { Data: { } data } f => new DataContent(data, f.MediaType) { Name = f.Filename },
            FilePart { Url: { } url } f => new UriContent(url, f.MediaType),
            FilePart { FileId: { } fileId } f => new HostedFileContent(fileId) { MediaType = f.MediaType, Name = f.Filename },
            ToolCallPart c => ToFunctionCall(c),
            ToolResultPart r => new FunctionResultContent(r.ToolCallId, r.Output),
            SourcePart s => new AIContent { Annotations = [ToCitation(s)] },
            _ => new AIContent(),
        };

        content.RawRepresentation = part;
        if (part is not ReasoningPart && part.ProviderMetadata is { Count: > 0 } metadata)
        {
            (content.AdditionalProperties ??= [])[ProviderMetadataKey] = JsonSerializer.SerializeToElement(metadata);
        }

        return content;
    }

    /// <summary>
    /// Converts a tool call into a <see cref="FunctionCallContent"/>. Calls the provider already executed are marked
    /// <see cref="FunctionCallContent.InformationalOnly"/> so <c>FunctionInvokingChatClient</c> does not run them, and
    /// an input that is not a JSON object is reported through <see cref="FunctionCallContent.Exception"/>.
    /// </summary>
    private static FunctionCallContent ToFunctionCall(ToolCallPart call)
    {
        var content = FunctionCallContent.CreateFromParsedArguments(call, call.ToolCallId, call.ToolName, ParseArguments);
        content.InformationalOnly = call.ProviderExecuted;
        return content;
    }

    private static Dictionary<string, object?>? ParseArguments(ToolCallPart call) => call switch
    {
        { InvalidInput: true } => throw new JsonException($"The model produced an invalid JSON input for tool '{call.ToolName}'."),
        { Input.ValueKind: JsonValueKind.Object } => call.Input.Deserialize<Dictionary<string, object?>>(AIJsonUtilities.DefaultOptions),
        { Input.ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => throw new JsonException($"The input of tool '{call.ToolName}' is not a JSON object."),
    };

    private static CitationAnnotation ToCitation(SourcePart source) => new()
    {
        Title = source.Title,
        Url = Uri.TryCreate(source.Url, UriKind.Absolute, out var url) ? url : null,
        Snippet = source.CitedText,
        RawRepresentation = source,
    };

    /// <summary>Converts AiSdk usage into <see cref="UsageDetails"/> (null when the model reported nothing).</summary>
    public static UsageDetails? ToUsageDetails(Usage usage) =>
        usage.InputTokens is null && usage.OutputTokens is null && usage.ReasoningTokens is null && usage.CachedInputTokens is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = usage.InputTokens,
                OutputTokenCount = usage.OutputTokens,
                TotalTokenCount = usage.TotalTokens,
                ReasoningTokenCount = usage.ReasoningTokens,
                CachedInputTokenCount = usage.CachedInputTokens,
            };

    /// <summary>
    /// Converts an AiSdk finish reason. <see cref="FinishReason.Error"/> and <see cref="FinishReason.Other"/> have no
    /// predefined counterpart and become the custom values <c>"error"</c> and <c>"other"</c>; <see cref="FinishReason.Unknown"/> is null.
    /// </summary>
    public static ChatFinishReason? ToChatFinishReason(FinishReason reason) => reason switch
    {
        FinishReason.Stop => ChatFinishReason.Stop,
        FinishReason.Length => ChatFinishReason.Length,
        FinishReason.ToolCalls => ChatFinishReason.ToolCalls,
        FinishReason.ContentFilter => ChatFinishReason.ContentFilter,
        FinishReason.Error => new ChatFinishReason("error"),
        FinishReason.Other => new ChatFinishReason("other"),
        _ => null,
    };

    // ---- Microsoft.Extensions.AI -> AiSdk ----

    /// <summary>
    /// Converts an <see cref="AIContent"/> into an AiSdk content part, or null when it has no equivalent (usage, errors...).
    /// When <see cref="AIContent.RawRepresentation"/> holds the original AiSdk part and it still matches the content,
    /// that part is reused as is, so provider metadata and flags survive the round trip.
    /// </summary>
    /// <param name="content">Content to convert.</param>
    /// <param name="inAssistantMessage">
    /// True for content produced by the model: a <see cref="FunctionResultContent"/> there is the result of a tool the
    /// provider executed, not one the caller ran.
    /// </param>
    /// <param name="toolNames">Tool names by call id, used to name tool results (Microsoft.Extensions.AI results only carry the id).</param>
    public static ContentPart? ToContentPart(AIContent content, bool inAssistantMessage, IReadOnlyDictionary<string, string> toolNames)
    {
        var raw = content.RawRepresentation as ContentPart;
        return content switch
        {
            TextContent t => raw is TextPart rt && rt.Text == t.Text
                ? rt
                : new TextPart(t.Text ?? string.Empty) { ProviderMetadata = ReadProviderMetadata(t) },
            TextReasoningContent r => raw is ReasoningPart rr && rr.Text == r.Text
                ? rr
                : new ReasoningPart(r.Text ?? string.Empty) { ProviderMetadata = DecodeProtectedData(r.ProtectedData) },
            DataContent d => raw is ImagePart or FilePart ? raw : d.HasTopLevelMediaType("image")
                ? new ImagePart { Data = d.Data.ToArray(), MediaType = d.MediaType, ProviderMetadata = ReadProviderMetadata(d) }
                : new FilePart { Data = d.Data.ToArray(), MediaType = d.MediaType, Filename = d.Name, ProviderMetadata = ReadProviderMetadata(d) },
            UriContent u => raw is ImagePart or FilePart ? raw : u.HasTopLevelMediaType("image")
                ? new ImagePart { Url = u.Uri, MediaType = u.MediaType, ProviderMetadata = ReadProviderMetadata(u) }
                : new FilePart { Url = u.Uri, MediaType = u.MediaType, ProviderMetadata = ReadProviderMetadata(u) },
            HostedFileContent h => raw is ImagePart or FilePart ? raw : h.HasTopLevelMediaType("image")
                ? new ImagePart { FileId = h.FileId, MediaType = h.MediaType!, ProviderMetadata = ReadProviderMetadata(h) }
                : new FilePart { FileId = h.FileId, MediaType = h.MediaType ?? "application/octet-stream", Filename = h.Name, ProviderMetadata = ReadProviderMetadata(h) },
            FunctionCallContent f => raw is ToolCallPart rc && rc.ToolCallId == f.CallId
                ? rc
                : new ToolCallPart(f.CallId, f.Name, ToArgumentsJson(f.Arguments))
                {
                    ProviderExecuted = f.InformationalOnly,
                    ProviderMetadata = ReadProviderMetadata(f),
                },
            FunctionResultContent r => raw is ToolResultPart rr && rr.ToolCallId == r.CallId
                ? rr
                : new ToolResultPart(r.CallId, toolNames.GetValueOrDefault(r.CallId, string.Empty), ToResultJson(r.Result))
                {
                    IsError = r.Exception is not null,
                    ProviderExecuted = inAssistantMessage,
                    ProviderMetadata = ReadProviderMetadata(r),
                },
            UsageContent or ErrorContent => null,
            _ => raw,
        };
    }

    /// <summary>Indexes the tool names of every function call, by call id.</summary>
    public static Dictionary<string, string> CollectToolNames(IEnumerable<AIContent> contents)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            names[call.CallId] = call.Name;
        }

        return names;
    }

    /// <summary>Converts <see cref="UsageDetails"/> into AiSdk usage.</summary>
    public static Usage FromUsageDetails(UsageDetails? usage) => usage is null
        ? Usage.Empty
        : new Usage
        {
            InputTokens = ToInt(usage.InputTokenCount),
            OutputTokens = ToInt(usage.OutputTokenCount),
            ReasoningTokens = ToInt(usage.ReasoningTokenCount),
            CachedInputTokens = ToInt(usage.CachedInputTokenCount),
        };

    /// <summary>Converts a <see cref="ChatFinishReason"/>, inferring tool calls when the client reported none.</summary>
    public static FinishReason FromChatFinishReason(ChatFinishReason? reason, bool hasToolCalls)
    {
        if (reason == ChatFinishReason.Stop || reason is null)
        {
            return hasToolCalls ? FinishReason.ToolCalls : reason is null ? FinishReason.Unknown : FinishReason.Stop;
        }

        if (reason == ChatFinishReason.Length)
        {
            return FinishReason.Length;
        }

        if (reason == ChatFinishReason.ToolCalls)
        {
            return FinishReason.ToolCalls;
        }

        if (reason == ChatFinishReason.ContentFilter)
        {
            return FinishReason.ContentFilter;
        }

        return string.Equals(reason.Value.Value, "error", StringComparison.OrdinalIgnoreCase) ? FinishReason.Error
            : hasToolCalls ? FinishReason.ToolCalls
            : FinishReason.Other;
    }

    /// <summary>Converts a Microsoft.Extensions.AI role; custom roles such as <c>"developer"</c> are mapped by name.</summary>
    public static ChatRole FromChatRole(MEAI.ChatRole role) =>
        role == MEAI.ChatRole.System ? ChatRole.System
        : role == MEAI.ChatRole.Assistant ? ChatRole.Assistant
        : role == MEAI.ChatRole.Tool ? ChatRole.Tool
        : string.Equals(role.Value, "developer", StringComparison.OrdinalIgnoreCase) ? ChatRole.System
        : ChatRole.User;

    /// <summary>Converts a float option without exposing binary noise (0.7f becomes 0.7, not 0.699999988079071).</summary>
    public static double? ToDouble(float? value) => value switch
    {
        null => null,
        { } v when float.IsFinite(v) => (double)(decimal)v,
        { } v => v,
    };

    /// <summary>Schema sent for a function declaration without one.</summary>
    public static JsonElement SchemaOrEmpty(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Undefined ? EmptyObjectSchema : schema;

    // ---- Provider metadata and reasoning protected data ----

    /// <summary>
    /// Encodes the provider metadata of a reasoning block (for example a signature) as the opaque
    /// <see cref="TextReasoningContent.ProtectedData"/>, which Microsoft.Extensions.AI preserves when it coalesces
    /// streaming updates. Data that came from a Microsoft.Extensions.AI client is returned unchanged.
    /// </summary>
    public static string? EncodeProtectedData(IReadOnlyDictionary<string, JsonElement>? metadata) => metadata switch
    {
        null or { Count: 0 } => null,
        { Count: 1 } when metadata.TryGetValue(ProtectedDataKey, out var data) && data.ValueKind == JsonValueKind.String => data.GetString(),
        _ => JsonSerializer.Serialize(metadata),
    };

    /// <summary>Reverses <see cref="EncodeProtectedData"/>: any other value is kept under <see cref="ProtectedDataKey"/>.</summary>
    public static IReadOnlyDictionary<string, JsonElement>? DecodeProtectedData(string? protectedData)
    {
        if (protectedData is null)
        {
            return null;
        }

        if (protectedData.StartsWith('{'))
        {
            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(protectedData) is { } metadata)
                {
                    return metadata;
                }
            }
            catch (JsonException)
            {
                // Not ours: keep it verbatim below.
            }
        }

        return new Dictionary<string, JsonElement> { [ProtectedDataKey] = JsonSerializer.SerializeToElement(protectedData) };
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadProviderMetadata(AIContent content)
    {
        if (content.AdditionalProperties?.TryGetValue(ProviderMetadataKey, out var value) != true)
        {
            return null;
        }

        return value switch
        {
            IReadOnlyDictionary<string, JsonElement> metadata => metadata,
            JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize<Dictionary<string, JsonElement>>(),
            _ => null,
        };
    }

    private static JsonElement ToArgumentsJson(IDictionary<string, object?>? arguments) => arguments is null
        ? JsonUtil.EmptyObject
        : JsonSerializer.SerializeToElement(arguments, AIJsonUtilities.DefaultOptions);

    /// <summary>
    /// Converts the result of a function into JSON. Strings and text content stay JSON strings (providers send them
    /// verbatim); anything else is serialized with <see cref="AIJsonUtilities.DefaultOptions"/>.
    /// </summary>
    private static JsonElement ToResultJson(object? result) => result switch
    {
        null => JsonNull,
        JsonElement element => element,
        string text => JsonSerializer.SerializeToElement(text),
        TextContent text => JsonSerializer.SerializeToElement(text.Text),
        IEnumerable<AIContent> contents when contents.All(c => c is TextContent) =>
            JsonSerializer.SerializeToElement(string.Concat(contents.Cast<TextContent>().Select(c => c.Text))),
        _ => JsonSerializer.SerializeToElement(result, result.GetType(), AIJsonUtilities.DefaultOptions),
    };

    private static int? ToInt(long? value) => value is null ? null : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
}
