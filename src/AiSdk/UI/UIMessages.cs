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

/// <summary>Opciones de <see cref="UIMessageConverter.ToModelMessages(IEnumerable{UIMessage}, UIMessageConverterOptions?)"/>.</summary>
/// <remarks>
/// Todo el historial lo envía el navegador y no es de confianza: el cliente puede escribir cualquier mensaje, incluidos
/// textos "del asistente" y resultados de herramientas. Las instrucciones y los datos sensibles deben vivir en el servidor
/// (instrucciones del agente, historial guardado con <c>AgentSession</c>), no en lo que reenvía el cliente.
/// </remarks>
public sealed class UIMessageConverterOptions
{
    /// <summary>
    /// Acepta mensajes con rol <c>system</c> enviados por el cliente. Por defecto <c>false</c>: se descartan, porque
    /// un usuario podría reemplazar o contradecir las instrucciones del agente.
    /// </summary>
    public bool AllowSystemMessages { get; init; }

    /// <summary>
    /// Herramientas que ejecuta el servidor. Sus partes en el historial (llamada y resultado) se descartan, porque el
    /// cliente podría falsificar sus resultados (p. ej. un "pago aprobado"). Deje fuera las herramientas que ejecuta el
    /// navegador (sin <c>execute</c> en el servidor), cuyo resultado sí debe llegar desde el cliente.
    /// </summary>
    public IReadOnlyCollection<string>? ServerExecutedTools { get; init; }
}

public static class UIMessageConverter
{
    /// <summary>
    /// Convierte mensajes de UI a mensajes de modelo (equivalente a <c>convertToModelMessages</c>).
    /// Los mensajes <c>system</c> del cliente se descartan y las partes inválidas (archivos con URL o data URL
    /// incorrectas, campos con tipos inesperados) se ignoran en lugar de producir un error 500.
    /// </summary>
    public static List<ModelMessage> ToModelMessages(IEnumerable<UIMessage> messages, UIMessageConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new UIMessageConverterOptions();
        var serverTools = options.ServerExecutedTools is { Count: > 0 } names ? new HashSet<string>(names, StringComparer.Ordinal) : null;
        var result = new List<ModelMessage>();
        foreach (var message in messages)
        {
            if (message is null)
            {
                continue;
            }

            var parts = message.Parts?.Where(p => p is not null).ToList() ?? [];
            switch (message.Role)
            {
                case "system" when options.AllowSystemMessages:
                    result.Add(ModelMessage.System(string.Concat(parts.Where(IsText).Select(p => Str(p["text"])))));
                    break;
                case "user":
                    result.Add(new ModelMessage(ChatRole.User, parts.Select(ToUserPart).OfType<ContentPart>().ToList()));
                    break;
                case "assistant":
                    AddAssistant(result, parts, serverTools);
                    break;
            }
        }

        return result;
    }

    private static bool IsText(JsonObject p) => Str(p["type"]) == "text";

    /// <summary>Valor de texto de un nodo JSON, o null si no es un string (el cliente puede enviar cualquier tipo).</summary>
    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static ContentPart? ToUserPart(JsonObject p)
    {
        switch (Str(p["type"]))
        {
            case "text":
                return new TextPart(Str(p["text"]) ?? string.Empty);
            case "file":
                // Una URL inválida es un error del cliente: la parte se descarta en lugar de lanzar (500).
                if (!TryParseFileUrl(Str(p["url"]), out var data, out var uri))
                {
                    return null;
                }

                var mediaType = Str(p["mediaType"]) ?? "application/octet-stream";
                return mediaType.StartsWith("image/", StringComparison.Ordinal)
                    ? new ImagePart { Data = data, Url = uri, MediaType = mediaType }
                    : new FilePart { Data = data, Url = uri, MediaType = mediaType, Filename = Str(p["filename"]) };
            default:
                return null;
        }
    }

    /// <summary>Acepta data URLs (base64 o texto) y URLs absolutas http(s); el resto se rechaza.</summary>
    private static bool TryParseFileUrl(string? url, out byte[]? data, out Uri? uri)
    {
        data = null;
        uri = null;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                return false;
            }

            var payload = url[(comma + 1)..];
            if (!url.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                data = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                return true;
            }

            try
            {
                data = Convert.FromBase64String(payload);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    private static void AddAssistant(List<ModelMessage> result, List<JsonObject> parts, HashSet<string>? serverTools)
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

        foreach (var part in parts)
        {
            var type = Str(part["type"]) ?? string.Empty;
            if (type == "step-start")
            {
                Flush();
            }
            else if (type == "text")
            {
                content.Add(new TextPart(Str(part["text"]) ?? string.Empty));
            }
            else if (type == "source-url")
            {
                content.Add(new SourcePart(Str(part["sourceId"]) ?? Guid.NewGuid().ToString("N"), Str(part["url"]) ?? string.Empty) { Title = Str(part["title"]) });
            }
            else if (type == "reasoning")
            {
                content.Add(new ReasoningPart(Str(part["text"]) ?? string.Empty));
            }
            else if (type.StartsWith("tool-", StringComparison.Ordinal) || type == "dynamic-tool")
            {
                var toolName = type == "dynamic-tool" ? Str(part["toolName"]) ?? "tool" : type["tool-".Length..];
                if (serverTools is not null && serverTools.Contains(toolName))
                {
                    // Resultado de una herramienta del servidor reenviado por el cliente: podría estar falsificado.
                    continue;
                }

                var id = Str(part["toolCallId"]) ?? Guid.NewGuid().ToString("N");
                var state = Str(part["state"]);
                var input = part["input"] is { } i ? JsonSerializer.SerializeToElement(i) : JsonUtil.EmptyObject;
                if (state is "input-available" or "output-available" or "output-error")
                {
                    content.Add(new ToolCallPart(id, toolName, input));
                }

                var providerExecuted = part["providerExecuted"] is JsonValue flag && flag.TryGetValue<bool>(out var executed) && executed;
                if (providerExecuted && content.Count > 0 && content[^1] is ToolCallPart last && last.ToolCallId == id)
                {
                    content[^1] = last with { ProviderExecuted = true };
                }

                ToolResultPart? toolResult = state switch
                {
                    "output-available" => new ToolResultPart(id, toolName, JsonSerializer.SerializeToElement(part["output"])) { ProviderExecuted = providerExecuted },
                    "output-error" => new ToolResultPart(id, toolName, JsonSerializer.SerializeToElement(Str(part["errorText"]))) { IsError = true, ProviderExecuted = providerExecuted },
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
/// Error de una herramienta tal como se pasa a <c>onError</c> al escribir el stream UI (<c>tool-output-error</c>).
/// Por defecto el navegador solo recibe un mensaje genérico, porque el texto de una excepción puede revelar detalles
/// internos; el modelo sí recibe el mensaje real. Para mostrarlo:
/// <c>onError: ex =&gt; ex is ToolErrorException t ? t.Message : "Se produjo un error."</c>.
/// </summary>
public sealed class ToolErrorException : AiSdkException
{
    public ToolErrorException(string toolName, string toolCallId, string message)
        : base(message)
    {
        ToolName = toolName;
        ToolCallId = toolCallId;
    }

    /// <summary>Herramienta que falló.</summary>
    public string ToolName { get; }

    /// <summary>Id de la llamada.</summary>
    public string ToolCallId { get; }
}

/// <summary>
/// Escribe un stream en formato <b>UI Message Stream</b> (SSE) compatible con
/// <c>useChat</c> del AI SDK (cabecera <c>x-vercel-ai-ui-message-stream: v1</c>).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Nombre del protocolo UI Message Stream del AI SDK; es una clase estática de utilidades, no un Stream.")]
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
                    // Mismo enmascarado que los demás errores: el texto real solo si onError decide mostrarlo.
                    ["errorText"] = ErrorText(new ToolErrorException(r.ToolResult.ToolName, r.ToolResult.ToolCallId, ToolErrorMessage(r.ToolResult.Output)), onError),
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
                ErrorPart e => new() { ["type"] = "error", ["errorText"] = ErrorText(e.Error, onError) },
                _ => null,
            };

            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    /// <summary>Texto de error que recibe el cliente: el de <paramref name="onError"/> o uno genérico.</summary>
    internal static string ErrorText(Exception error, Func<Exception, string>? onError) => onError?.Invoke(error) ?? DefaultErrorText;

    internal const string DefaultErrorText = "Se produjo un error.";

    private static string ToolErrorMessage(JsonElement output) =>
        output.ValueKind switch
        {
            JsonValueKind.String => output.GetString() ?? string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => output.GetRawText(),
        };

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
