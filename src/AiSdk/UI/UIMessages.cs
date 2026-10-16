using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AiSdk.Internal;

namespace AiSdk.UI;

/// <summary>
/// Mensaje de interfaz tal como lo envía <c>useChat</c> de AI SDK UI (React, Vue, Svelte...).
/// Permite usar un backend .NET con los frontends del AI SDK.
/// </summary>
public sealed class UIMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("parts")]
    public List<JsonObject> Parts { get; set; } = [];

    [JsonPropertyName("metadata")]
    public JsonNode? Metadata { get; set; }
}

/// <summary>Cuerpo de la petición que envía <c>useChat</c> (<c>DefaultChatTransport</c>).</summary>
public sealed class UIChatRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("messages")]
    public List<UIMessage> Messages { get; set; } = [];

    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }
}

public static class UIMessageConverter
{
    /// <summary>Convierte mensajes de UI a mensajes de modelo (equivalente a <c>convertToModelMessages</c>).</summary>
    public static List<ModelMessage> ToModelMessages(IEnumerable<UIMessage> messages)
    {
        var result = new List<ModelMessage>();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "system":
                    result.Add(ModelMessage.System(string.Concat(message.Parts.Where(IsText).Select(p => (string?)p["text"]))));
                    break;
                case "user":
                    result.Add(new ModelMessage(ChatRole.User, message.Parts.Select(ToUserPart).OfType<ContentPart>().ToList()));
                    break;
                case "assistant":
                    AddAssistant(result, message);
                    break;
            }
        }

        return result;
    }

    private static bool IsText(JsonObject p) => (string?)p["type"] == "text";

    private static ContentPart? ToUserPart(JsonObject p)
    {
        switch ((string?)p["type"])
        {
            case "text":
                return new TextPart((string?)p["text"] ?? string.Empty);
            case "file":
                var mediaType = (string?)p["mediaType"] ?? "application/octet-stream";
                var url = (string?)p["url"] ?? string.Empty;
                byte[]? data = null;
                Uri? uri = null;
                if (url.StartsWith("data:", StringComparison.Ordinal))
                {
                    var comma = url.IndexOf(',');
                    data = Convert.FromBase64String(url[(comma + 1)..]);
                }
                else
                {
                    uri = new Uri(url);
                }

                return mediaType.StartsWith("image/", StringComparison.Ordinal)
                    ? new ImagePart { Data = data, Url = uri, MediaType = mediaType }
                    : new FilePart { Data = data, Url = uri, MediaType = mediaType, Filename = (string?)p["filename"] };
            default:
                return null;
        }
    }

    private static void AddAssistant(List<ModelMessage> result, UIMessage message)
    {
        var content = new List<ContentPart>();
        var toolResults = new List<ToolResultPart>();

        void Flush()
        {
            if (content.Count > 0)
            {
                result.Add(new ModelMessage(ChatRole.Assistant, content.ToList()));
            }

            if (toolResults.Count > 0)
            {
                result.Add(new ModelMessage(ChatRole.Tool, toolResults.ToList()));
            }

            content.Clear();
            toolResults.Clear();
        }

        foreach (var part in message.Parts)
        {
            var type = (string?)part["type"] ?? string.Empty;
            if (type == "step-start")
            {
                Flush();
            }
            else if (type == "text")
            {
                content.Add(new TextPart((string?)part["text"] ?? string.Empty));
            }
            else if (type == "source-url")
            {
                content.Add(new SourcePart((string?)part["sourceId"] ?? Guid.NewGuid().ToString("N"), (string?)part["url"] ?? string.Empty) { Title = (string?)part["title"] });
            }
            else if (type == "reasoning")
            {
                content.Add(new ReasoningPart((string?)part["text"] ?? string.Empty));
            }
            else if (type.StartsWith("tool-", StringComparison.Ordinal) || type == "dynamic-tool")
            {
                var toolName = type == "dynamic-tool" ? (string?)part["toolName"] ?? "tool" : type["tool-".Length..];
                var id = (string?)part["toolCallId"] ?? Guid.NewGuid().ToString("N");
                var state = (string?)part["state"];
                var input = part["input"] is { } i ? JsonSerializer.SerializeToElement(i) : JsonUtil.EmptyObject;
                if (state is "input-available" or "output-available" or "output-error")
                {
                    content.Add(new ToolCallPart(id, toolName, input));
                }

                var providerExecuted = (bool?)part["providerExecuted"] == true;
                if (providerExecuted && content.Count > 0 && content[^1] is ToolCallPart last && last.ToolCallId == id)
                {
                    content[^1] = last with { ProviderExecuted = true };
                }

                ToolResultPart? toolResult = state switch
                {
                    "output-available" => new ToolResultPart(id, toolName, JsonSerializer.SerializeToElement(part["output"])) { ProviderExecuted = providerExecuted },
                    "output-error" => new ToolResultPart(id, toolName, JsonSerializer.SerializeToElement((string?)part["errorText"])) { IsError = true, ProviderExecuted = providerExecuted },
                    _ => null,
                };
                if (toolResult is not null)
                {
                    // Los resultados de herramientas del proveedor forman parte del mensaje del asistente.
                    if (providerExecuted)
                    {
                        content.Add(toolResult);
                    }
                    else
                    {
                        toolResults.Add(toolResult);
                    }
                }
            }
        }

        Flush();
    }
}

/// <summary>
/// Escribe un stream en formato <b>UI Message Stream</b> (SSE) compatible con
/// <c>useChat</c> del AI SDK (cabecera <c>x-vercel-ai-ui-message-stream: v1</c>).
/// </summary>
public static partial class UIMessageStream
{
    public const string HeaderName = "x-vercel-ai-ui-message-stream";
    public const string HeaderValue = "v1";
    public const string ContentType = "text/event-stream";

    /// <summary>Convierte las partes del stream en chunks JSON del protocolo.</summary>
    public static async IAsyncEnumerable<JsonObject> ToChunks(IAsyncEnumerable<StreamPart> parts, string? messageId = null, Func<Exception, string>? onError = null)
    {
        await foreach (var part in parts.ConfigureAwait(false))
        {
            JsonObject? chunk = part switch
            {
                StartPart => messageId is null ? new() { ["type"] = "start" } : new() { ["type"] = "start", ["messageId"] = messageId },
                StartStepPart => new() { ["type"] = "start-step" },
                TextStartPart t => new() { ["type"] = "text-start", ["id"] = t.Id },
                TextDeltaPart t => new() { ["type"] = "text-delta", ["id"] = t.Id, ["delta"] = t.Delta },
                TextEndPart t => new() { ["type"] = "text-end", ["id"] = t.Id },
                ReasoningStartPart r => new() { ["type"] = "reasoning-start", ["id"] = r.Id },
                ReasoningDeltaPart r => new() { ["type"] = "reasoning-delta", ["id"] = r.Id, ["delta"] = r.Delta },
                ReasoningEndPart r => new() { ["type"] = "reasoning-end", ["id"] = r.Id },
                ToolInputStartPart t => new() { ["type"] = "tool-input-start", ["toolCallId"] = t.ToolCallId, ["toolName"] = t.ToolName },
                ToolInputDeltaPart t => new() { ["type"] = "tool-input-delta", ["toolCallId"] = t.ToolCallId, ["inputTextDelta"] = t.Delta },
                ToolCallStreamPart c => WithProviderFlag(new()
                {
                    ["type"] = "tool-input-available",
                    ["toolCallId"] = c.ToolCall.ToolCallId,
                    ["toolName"] = c.ToolCall.ToolName,
                    ["input"] = JsonUtil.ToNode(c.ToolCall.Input),
                }, c.ToolCall.ProviderExecuted),
                ToolResultStreamPart { ToolResult.IsError: true } r => new()
                {
                    ["type"] = "tool-output-error",
                    ["toolCallId"] = r.ToolResult.ToolCallId,
                    ["errorText"] = r.ToolResult.Output.ValueKind == JsonValueKind.String ? r.ToolResult.Output.GetString() : r.ToolResult.Output.GetRawText(),
                },
                ToolResultStreamPart r => WithProviderFlag(new()
                {
                    ["type"] = "tool-output-available",
                    ["toolCallId"] = r.ToolResult.ToolCallId,
                    ["output"] = JsonUtil.ToNode(r.ToolResult.Output),
                }, r.ToolResult.ProviderExecuted),
                SourceStreamPart src => SourceChunk(src.Source),
                FinishStepPart => new() { ["type"] = "finish-step" },
                FinishPart => new() { ["type"] = "finish" },
                AbortPart => new() { ["type"] = "abort" },
                DataStreamPart d => DataChunk(d.Type, d.Data, d.Id, d.Transient),
                ErrorPart e => new() { ["type"] = "error", ["errorText"] = onError?.Invoke(e.Error) ?? "Se produjo un error." },
                _ => null,
            };

            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    internal static JsonObject DataChunk(string type, JsonNode? data, string? id, bool transient)
    {
        var chunk = new JsonObject { ["type"] = "data-" + type };
        if (id is not null)
        {
            chunk["id"] = id;
        }

        chunk["data"] = data?.DeepClone();
        if (transient)
        {
            chunk["transient"] = true;
        }

        return chunk;
    }

    private static JsonObject WithProviderFlag(JsonObject chunk, bool providerExecuted)
    {
        if (providerExecuted)
        {
            chunk["providerExecuted"] = true;
        }

        return chunk;
    }

    private static JsonObject SourceChunk(SourcePart source)
    {
        var chunk = new JsonObject { ["type"] = "source-url", ["sourceId"] = source.Id, ["url"] = source.Url };
        if (source.Title is not null)
        {
            chunk["title"] = source.Title;
        }

        return chunk;
    }

    /// <summary>Escribe el stream SSE completo (incluido <c>data: [DONE]</c>) en <paramref name="output"/>.</summary>
    public static Task WriteAsync(
        IAsyncEnumerable<StreamPart> parts,
        Stream output,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        CancellationToken cancellationToken = default) =>
        WriteChunksAsync(ToChunks(parts, messageId, onError), output, cancellationToken);
}
