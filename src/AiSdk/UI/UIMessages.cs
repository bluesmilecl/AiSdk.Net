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

    /// <summary>
    /// Verifica en <see cref="UIMessageConverter.ToModelInput"/> las respuestas de aprobación que devuelve <c>useChat</c>
    /// (partes en estado <c>approval-responded</c>). Debe ser el mismo protector (clave y ámbito) con el que se escribió
    /// el stream que pidió la aprobación. Sin protector ninguna aprobación es válida: las llamadas se cierran con un error
    /// y no se ejecutan.
    /// </summary>
    public ToolApprovalProtector? ToolApprovals { get; init; }
}

/// <summary>
/// Conversación de <c>useChat</c> convertida para el modelo por <see cref="UIMessageConverter.ToModelInput"/>: los mensajes
/// y las decisiones de aprobación verificadas, que se pasan juntos al agente
/// (<see cref="AgentCallOptions.Messages"/> y <see cref="AgentCallOptions.ToolApprovals"/>).
/// </summary>
public sealed class UIModelInput
{
    /// <summary>Mensajes para el modelo.</summary>
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    /// <summary>
    /// Decisiones (aprobar o denegar) cuyo approvalId verificó. Es null si el último mensaje no responde aprobaciones;
    /// si las responde, nunca es null (aunque esté vacía): las llamadas sin una decisión válida se cierran con un error.
    /// </summary>
    public IReadOnlyList<ToolApprovalResponse>? ToolApprovals { get; init; }
}

public static class UIMessageConverter
{
    /// <summary>
    /// Convierte mensajes de UI a mensajes de modelo (equivalente a <c>convertToModelMessages</c>).
    /// Los mensajes <c>system</c> del cliente se descartan y las partes inválidas (archivos con URL o data URL
    /// incorrectas, campos con tipos inesperados) se ignoran en lugar de producir un error 500. Las respuestas de
    /// aprobación (<c>approval-responded</c>) se descartan: para reanudar aprobaciones use <see cref="ToModelInput"/>.
    /// </summary>
    public static List<ModelMessage> ToModelMessages(IEnumerable<UIMessage> messages, UIMessageConverterOptions? options = null) =>
        ConvertMessages(messages, options ?? new UIMessageConverterOptions(), collectApprovals: false).Messages;

    /// <summary>
    /// Como <see cref="ToModelMessages"/>, y además reanuda las aprobaciones de herramientas: si el último mensaje es del
    /// asistente y su último paso tiene partes en estado <c>approval-responded</c> (<c>addToolApprovalResponse</c>),
    /// devuelve sus llamadas y decisiones para el motor. Una respuesta solo cuenta si su <c>approvalId</c> verifica con
    /// <see cref="UIMessageConverterOptions.ToolApprovals"/> contra la llamada reenviada (mismo id, herramienta y
    /// entrada): una aprobación inventada, de otro usuario o chat, expirada o con la entrada cambiada no se ejecuta, y el
    /// modelo recibe un error. Las llamadas aprobadas se ejecutan con la entrada que firmó el servidor.
    /// </summary>
    /// <example>
    /// <code>
    /// var input = UIMessageConverter.ToModelInput(request.Messages, new() { ToolApprovals = approvals, ServerExecutedTools = ["send_email"] });
    /// var stream = agent.Stream(new AgentCallOptions { Messages = input.Messages, ToolApprovals = input.ToolApprovals, DeferToolApprovals = true });
    /// return stream.ToUIMessageStreamResult(toolApprovals: approvals);
    /// </code>
    /// </example>
    public static UIModelInput ToModelInput(IEnumerable<UIMessage> messages, UIMessageConverterOptions? options = null)
    {
        var (result, approvals) = ConvertMessages(messages, options ?? new UIMessageConverterOptions(), collectApprovals: true);
        return new UIModelInput { Messages = result, ToolApprovals = approvals };
    }

    private static (List<ModelMessage> Messages, List<ToolApprovalResponse>? Approvals) ConvertMessages(
        IEnumerable<UIMessage> messages,
        UIMessageConverterOptions options,
        bool collectApprovals)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var serverTools = options.ServerExecutedTools is { Count: > 0 } names ? new HashSet<string>(names, StringComparer.Ordinal) : null;
        var list = messages.Where(m => m is not null).ToList();
        var result = new List<ModelMessage>();
        List<ToolApprovalResponse>? approvals = null;
        for (var i = 0; i < list.Count; i++)
        {
            var message = list[i];
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
                    // Solo se reanudan las aprobaciones del último paso del último mensaje: las demás ya no están pendientes.
                    var approvalsFrom = collectApprovals && i == list.Count - 1
                        ? parts.FindLastIndex(p => Str(p["type"]) == "step-start") + 1
                        : int.MaxValue;
                    AddAssistant(result, parts, serverTools, approvalsFrom, options.ToolApprovals, ref approvals);
                    break;
            }
        }

        return (result, approvals);
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

    private static void AddAssistant(
        List<ModelMessage> result,
        List<JsonObject> parts,
        HashSet<string>? serverTools,
        int approvalsFrom,
        ToolApprovalProtector? protector,
        ref List<ToolApprovalResponse>? approvals)
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

        var approvalCalls = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
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
                var state = Str(part["state"]);
                var input = part["input"] is { } i ? JsonSerializer.SerializeToElement(i) : JsonUtil.EmptyObject;
                if (state == "approval-responded")
                {
                    // Respuesta a una aprobación (addToolApprovalResponse). Se reanuda solo si sigue pendiente (último paso
                    // del último mensaje); vale incluso para herramientas del servidor, porque su approvalId lo verifica.
                    if (index >= approvalsFrom && Str(part["toolCallId"]) is { Length: > 0 } callId && approvalCalls.Add(callId))
                    {
                        var call = new ToolCallPart(callId, toolName, input);
                        content.Add(call);
                        approvals ??= [];
                        if (ApprovalDecision(part, call, protector) is { } decision)
                        {
                            approvals.Add(decision);
                        }
                    }

                    continue;
                }

                if (serverTools is not null && serverTools.Contains(toolName))
                {
                    // Resultado de una herramienta del servidor reenviado por el cliente: podría estar falsificado.
                    continue;
                }

                var id = Str(part["toolCallId"]) ?? Guid.NewGuid().ToString("N");
                if (state is "input-available" or "output-available" or "output-error" or "output-denied")
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
                    "output-denied" => GenerationEngine.Denied(new ToolCallPart(id, toolName, input), Str((part["approval"] as JsonObject)?["reason"])) with { ProviderExecuted = providerExecuted },
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

    /// <summary>Decisión de una parte <c>approval-responded</c>, o null si su approvalId no verifica para esa llamada.</summary>
    private static ToolApprovalResponse? ApprovalDecision(JsonObject part, ToolCallPart call, ToolApprovalProtector? protector)
    {
        if (protector is null
            || part["approval"] is not JsonObject approval
            || approval["approved"] is not JsonValue flag
            || !flag.TryGetValue<bool>(out var approved)
            || !protector.VerifyApprovalId(Str(approval["id"]), call))
        {
            return null;
        }

        // Como en el AI SDK, el motivo solo llega al modelo cuando se deniega.
        return new ToolApprovalResponse(call.ToolCallId, approved, approved ? null : Str(approval["reason"]));
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
    /// <param name="parts">Partes del stream (<see cref="StreamTextResult.FullStream"/>).</param>
    /// <param name="messageId">Id del mensaje del asistente (chunk <c>start</c>).</param>
    /// <param name="onError">Texto de error que ve el cliente (por defecto uno genérico).</param>
    /// <param name="toolApprovals">
    /// Emite los <c>approvalId</c> de las llamadas pendientes de aprobación (chunk <c>tool-approval-request</c>). Sin él,
    /// una llamada que quedó pendiente se informa como error (<c>tool-output-error</c>): el servidor no podría verificar
    /// la respuesta del usuario.
    /// </param>
    public static async IAsyncEnumerable<JsonObject> ToChunks(
        IAsyncEnumerable<StreamPart> parts,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        ToolApprovalProtector? toolApprovals = null)
    {
        // Entrada que ya vio el cliente de cada llamada: si se reparó antes de pedir la aprobación, se vuelve a enviar.
        var streamedInputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await foreach (var part in parts.ConfigureAwait(false))
        {
            if (part is ToolApprovalRequestStreamPart approval)
            {
                foreach (var approvalChunk in ApprovalChunks(approval.Request.ToolCall, streamedInputs, onError, toolApprovals))
                {
                    yield return approvalChunk;
                }

                continue;
            }

            if (part is ToolCallStreamPart streamed)
            {
                streamedInputs[streamed.ToolCall.ToolCallId] = streamed.ToolCall.Input;
            }

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
                ToolOutputDeniedStreamPart d => new() { ["type"] = "tool-output-denied", ["toolCallId"] = d.ToolResult.ToolCallId },
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

    /// <summary>
    /// Chunks de una llamada pendiente de aprobación. El chunk <c>tool-approval-request</c> lleva solo
    /// <c>approvalId</c> y <c>toolCallId</c>: las versiones 6.0.x de <c>ai</c> validan ese chunk como objeto estricto
    /// y rechazan cualquier otro campo.
    /// </summary>
    private static IEnumerable<JsonObject> ApprovalChunks(
        ToolCallPart call,
        Dictionary<string, JsonElement> streamedInputs,
        Func<Exception, string>? onError,
        ToolApprovalProtector? protector)
    {
        if (protector is null)
        {
            // Falla cerrado: una aprobación que el servidor no podrá verificar nunca se ejecutaría.
            var error = new ToolErrorException(call.ToolName, call.ToolCallId,
                "La herramienta requiere aprobación, pero el stream UI no tiene un ToolApprovalProtector (toolApprovals) para pedirla; no se ejecutó.");
            yield return new JsonObject { ["type"] = "tool-output-error", ["toolCallId"] = call.ToolCallId, ["errorText"] = ErrorText(error, onError) };
            yield break;
        }

        // El usuario debe ver exactamente la entrada que aprueba (la firmada), aunque se haya reparado tras emitirla.
        if (!streamedInputs.TryGetValue(call.ToolCallId, out var shown) || !JsonElement.DeepEquals(shown, call.Input))
        {
            yield return new JsonObject
            {
                ["type"] = "tool-input-available",
                ["toolCallId"] = call.ToolCallId,
                ["toolName"] = call.ToolName,
                ["input"] = JsonUtil.ToNode(call.Input),
            };
        }

        yield return new JsonObject
        {
            ["type"] = "tool-approval-request",
            ["approvalId"] = protector.CreateApprovalId(call),
            ["toolCallId"] = call.ToolCallId,
        };
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
    /// <remarks><paramref name="toolApprovals"/>: ver <see cref="ToChunks"/>.</remarks>
    public static Task WriteAsync(
        IAsyncEnumerable<StreamPart> parts,
        Stream output,
        string? messageId = null,
        Func<Exception, string>? onError = null,
        ToolApprovalProtector? toolApprovals = null,
        CancellationToken cancellationToken = default) =>
        WriteChunksAsync(ToChunks(parts, messageId, onError, toolApprovals), output, cancellationToken);
}
