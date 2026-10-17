using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.Anthropic;

/// <summary>Modelo de lenguaje sobre la Messages API de Anthropic (<c>POST /v1/messages</c>).</summary>
public sealed class AnthropicMessagesLanguageModel : ILanguageModel, ITokenCountingModel
{
    public const string SignatureKey = "anthropic.signature";
    public const string RedactedDataKey = "anthropic.redactedData";

    /// <summary>Bloque original de la API, reenviado tal cual en turnos siguientes (herramientas de servidor).</summary>
    public const string RawBlockKey = "anthropic.rawBlock";

    /// <summary><c>stop_reason</c> original de la API.</summary>
    public const string StopReasonKey = "anthropic.stopReason";

    /// <summary>Máximo de reanudaciones automáticas tras <c>pause_turn</c>.</summary>
    private const int MaxPauseTurnContinuations = 10;

    private readonly AnthropicProvider _provider;

    internal AnthropicMessagesLanguageModel(string modelId, AnthropicProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    // ------------------------------------------------------------------
    // Generate (con reanudación de pause_turn)
    // ------------------------------------------------------------------

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var content = new List<ContentPart>();
        var usage = Usage.Empty;
        var warnings = new List<string>();
        for (var round = 0; ; round++)
        {
            var (body, betas, bodyWarnings) = BuildBody(request, stream: false);
            warnings.AddRange(bodyWarnings);
            using var http = ProviderHttp.CreateJsonRequest(_provider.Url("messages"), body, _provider.Headers(request.Headers, betas), stream: false);
            using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
            var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var result = ParseResponse(json);

            content.AddRange(result.Content);
            usage += result.Usage;
            var paused = (string?)json["stop_reason"] == "pause_turn";
            if (!paused || round >= MaxPauseTurnContinuations)
            {
                return result with { Content = content, Usage = usage, Warnings = warnings.Distinct().ToList() };
            }

            // pause_turn: una herramienta de servidor necesita más tiempo; se reenvía el turno para continuar.
            request = request with { Messages = [.. request.Messages, new ModelMessage(ChatRole.Assistant, result.Content)] };
        }
    }

    /// <summary>Cuenta los tokens de entrada con <c>/v1/messages/count_tokens</c> (no consume la cuota de generación).</summary>
    public async Task<int> CountTokensAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, betas, _) = BuildBody(request, stream: false);
        var countBody = new JsonObject();
        foreach (var key in new[] { "model", "messages", "system", "tools", "tool_choice", "thinking" })
        {
            if (body[key] is { } value)
            {
                countBody[key] = value.DeepClone();
            }
        }

        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("messages/count_tokens"), countBody, _provider.Headers(request.Headers, betas), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return (int?)json["input_tokens"] ?? 0;
    }

    // ------------------------------------------------------------------
    // Stream (con reanudación de pause_turn)
    // ------------------------------------------------------------------

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new StreamState();
        for (var round = 0; ; round++)
        {
            state.BeginRound(round);
            await foreach (var part in StreamRoundAsync(request, state, cancellationToken).ConfigureAwait(false))
            {
                yield return part;
                if (part is ErrorPart)
                {
                    yield break;
                }
            }

            if (state.RawStopReason != "pause_turn" || round >= MaxPauseTurnContinuations)
            {
                break;
            }

            request = request with { Messages = [.. request.Messages, new ModelMessage(ChatRole.Assistant, state.RoundContent.BuildContent())] };
        }

        yield return new ModelFinishPart(state.Finish, state.Usage);
    }

    private async IAsyncEnumerable<StreamPart> StreamRoundAsync(LanguageModelRequest request, StreamState state, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (body, betas, _) = BuildBody(request, stream: true);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("messages"), body, _provider.Headers(request.Headers, betas), stream: true);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: true, cancellationToken).ConfigureAwait(false);

        var blocks = new Dictionary<int, BlockState>();
        var roundUsage = Usage.Empty;

        // Toda parte emitida también se acumula para poder reenviar el turno tras pause_turn.
        StreamPart Track(StreamPart part)
        {
            state.RoundContent.Add(part);
            return part;
        }

        await foreach (var sse in ProviderHttp.ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (JsonNode.Parse(sse.Data) is not JsonObject evt)
            {
                continue;
            }

            switch ((string?)evt["type"])
            {
                case "message_start":
                    var message = evt["message"] as JsonObject;
                    if (state.Round == 0)
                    {
                        yield return new ResponseMetadataPart(new ResponseMetadata((string?)message?["id"], (string?)message?["model"], DateTimeOffset.UtcNow));
                    }

                    if (message?["container"] is JsonObject startContainer && (string?)startContainer["id"] is { } startContainerId && startContainerId != state.ContainerId)
                    {
                        state.ContainerId = startContainerId;
                        yield return new ContentStreamPart(ContainerMarker(startContainerId, startContainer["expires_at"]?.ToString()));
                    }

                    if (message?["usage"] is JsonObject startUsage)
                    {
                        roundUsage = ParseUsage(startUsage);
                    }

                    break;

                case "content_block_start":
                {
                    var index = (int?)evt["index"] ?? 0;
                    var block = evt["content_block"] as JsonObject ?? [];
                    var type = (string?)block["type"] ?? "unknown";
                    var blockState = new BlockState(type, block)
                    {
                        Id = (string?)block["id"],
                        Name = (string?)block["name"],
                        RedactedData = (string?)block["data"],
                    };
                    blocks[index] = blockState;

                    if (type is "tool_use" or "server_tool_use" or "mcp_tool_use")
                    {
                        state.ToolNames[blockState.Id ?? string.Empty] = blockState.Name ?? type;
                        yield return Track(new ToolInputStartPart(blockState.Id!, blockState.Name!));
                    }
                    else if (type == "text" && (string?)block["text"] is { Length: > 0 } initial)
                    {
                        yield return Track(new TextDeltaPart(state.Id("text", index), initial));
                    }
                    else if (IsServerToolResult(type))
                    {
                        // Los resultados de herramientas de servidor llegan completos en el inicio del bloque.
                        foreach (var part in ServerToolResultParts(block, state.ToolNames, state.SeenSources))
                        {
                            yield return Track(part switch
                            {
                                ToolResultPart r => new ToolResultStreamPart(r),
                                SourcePart s => new SourceStreamPart(s),
                                _ => new ContentStreamPart(part),
                            });
                        }
                    }

                    break;
                }

                case "content_block_delta":
                {
                    var index = (int?)evt["index"] ?? 0;
                    var delta = evt["delta"] as JsonObject;
                    blocks.TryGetValue(index, out var blockState);
                    switch ((string?)delta?["type"])
                    {
                        case "text_delta":
                            yield return Track(new TextDeltaPart(state.Id("text", index), (string?)delta["text"] ?? string.Empty));
                            break;
                        case "thinking_delta":
                            if ((string?)delta["thinking"] is { Length: > 0 } thinking)
                            {
                                yield return Track(new ReasoningDeltaPart(state.Id("reasoning", index), thinking));
                            }

                            break;
                        case "signature_delta":
                            if (blockState is not null)
                            {
                                blockState.Signature = (string?)delta["signature"];
                            }

                            break;
                        case "input_json_delta":
                            var partial = (string?)delta["partial_json"] ?? string.Empty;
                            blockState?.Json.Append(partial);
                            if (blockState?.Id is not null && partial.Length > 0)
                            {
                                yield return Track(new ToolInputDeltaPart(blockState.Id, partial));
                            }

                            break;
                        case "citations_delta":
                            if (delta["citation"] is JsonObject citation && SourceFromCitation(citation, state.SeenSources) is { } source)
                            {
                                yield return Track(new SourceStreamPart(source));
                            }

                            break;
                    }

                    break;
                }

                case "content_block_stop":
                {
                    var index = (int?)evt["index"] ?? 0;
                    if (!blocks.TryGetValue(index, out var blockState))
                    {
                        break;
                    }

                    switch (blockState.Type)
                    {
                        case "tool_use":
                        {
                            var ok = JsonUtil.TryParseElement(blockState.Json.ToString(), out var input);
                            yield return Track(new ToolCallStreamPart(new ToolCallPart(blockState.Id!, blockState.Name!, input) { InvalidInput = !ok }));
                            break;
                        }

                        case "server_tool_use" or "mcp_tool_use":
                        {
                            JsonUtil.TryParseElement(blockState.Json.ToString(), out var input);
                            var raw = (JsonObject)blockState.Start.DeepClone();
                            raw["input"] = JsonUtil.ToNode(input);
                            yield return Track(new ToolCallStreamPart(ServerToolCall(raw)));
                            break;
                        }

                        case "thinking" or "redacted_thinking":
                            yield return Track(new ReasoningEndPart(state.Id("reasoning", index))
                            {
                                ProviderMetadata = ReasoningMetadata(blockState.Signature, blockState.RedactedData),
                            });
                            break;

                        case "text":
                            break;

                        default:
                            if (!IsServerToolResult(blockState.Type))
                            {
                                // Bloque desconocido (p. ej. de una beta): se conserva para reenviarlo.
                                yield return Track(new ContentStreamPart(new RawProviderPart("anthropic", JsonSerializer.SerializeToElement(blockState.Start))));
                            }

                            break;
                    }

                    break;
                }

                case "message_delta":
                    if (evt["delta"]?["container"] is JsonObject deltaContainer && (string?)deltaContainer["id"] is { } deltaContainerId && deltaContainerId != state.ContainerId)
                    {
                        state.ContainerId = deltaContainerId;
                        yield return new ContentStreamPart(ContainerMarker(deltaContainerId, deltaContainer["expires_at"]?.ToString()));
                    }

                    if ((string?)evt["delta"]?["stop_reason"] is { } stop)
                    {
                        state.RawStopReason = stop;
                        state.Finish = MapStopReason(stop);
                    }

                    if (evt["usage"] is JsonObject deltaUsage)
                    {
                        roundUsage = roundUsage with { OutputTokens = (int?)deltaUsage["output_tokens"] ?? roundUsage.OutputTokens };
                    }

                    break;

                case "error":
                    var error = evt["error"] as JsonObject;
                    var errorType = (string?)error?["type"];
                    yield return new ErrorPart(new ApiCallException(
                        $"{errorType}: {(string?)error?["message"]}",
                        errorType == "overloaded_error" ? 529 : 500,
                        sse.Data,
                        _provider.Url("messages")));
                    yield break;
            }
        }

        state.Usage += roundUsage;
    }

    // ------------------------------------------------------------------
    // Petición
    // ------------------------------------------------------------------

    internal (JsonObject Body, List<string> Betas, List<string> Warnings) BuildBody(LanguageModelRequest request, bool stream)
    {
        var options = _provider.Options;
        var warnings = new List<string>();
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["max_tokens"] = request.MaxOutputTokens ?? (stream ? options.DefaultStreamingMaxTokens : options.DefaultMaxTokens),
        };

        if (system.Length > 0)
        {
            body["system"] = system;
        }

        body["messages"] = ConvertMessages(request.Messages);

        // Nota: los modelos Claude más recientes rechazan temperature/top_p/top_k con valores no por defecto;
        // solo se envían si el llamador los especifica.
        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.TopK is { } topK)
        {
            body["top_k"] = topK;
        }

        if (request.StopSequences is { Count: > 0 } stop)
        {
            body["stop_sequences"] = new JsonArray(stop.Select(s => (JsonNode?)s).ToArray());
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            var toolArray = new JsonArray();
            foreach (var t in tools)
            {
                if (t.IsProviderTool)
                {
                    if (string.Equals(t.ProviderName, "anthropic", StringComparison.OrdinalIgnoreCase))
                    {
                        toolArray.Add(t.ProviderDefinition!.DeepClone());
                    }
                    else
                    {
                        warnings.Add($"La herramienta '{t.Name}' es de '{t.ProviderName}' y se omitió en Anthropic.");
                    }

                    continue;
                }

                var tool = new JsonObject
                {
                    ["name"] = t.Name,
                    ["input_schema"] = JsonUtil.ToNode(t.Strict ? JsonSchema.ToStrict(t.InputSchema) : t.InputSchema),
                };
                if (t.Description is not null)
                {
                    tool.Insert(1, "description", t.Description);
                }

                if (t.Strict)
                {
                    tool["strict"] = true;
                }

                if (stream && options.EagerInputStreaming)
                {
                    tool["eager_input_streaming"] = true;
                }

                toolArray.Add(tool);
            }

            if (toolArray.Count > 0)
            {
                body["tools"] = toolArray;
                if (request.ToolChoice is { } choice)
                {
                    body["tool_choice"] = choice.Kind switch
                    {
                        ToolChoiceKind.Auto => new JsonObject { ["type"] = "auto" },
                        ToolChoiceKind.None => new JsonObject { ["type"] = "none" },
                        ToolChoiceKind.Required => new JsonObject { ["type"] = "any" },
                        _ => new JsonObject { ["type"] = "tool", ["name"] = choice.ToolName },
                    };
                }
            }
        }

        if (request.ResponseFormat is { Type: ResponseFormatType.Json, Schema: { } schema })
        {
            body["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = JsonUtil.ToNode(JsonSchema.ToStrict(schema)) },
            };
        }

        if (stream)
        {
            body["stream"] = true;
        }

        var betas = new List<string>();
        var providerOptions = request.ProviderOptions?.For("anthropic")?.DeepClone() as JsonObject;
        if (providerOptions?["betas"] is JsonArray betaArray)
        {
            betas.AddRange(betaArray.Select(b => (string?)b).OfType<string>());
            providerOptions.Remove("betas");
        }

        JsonUtil.DeepMerge(body, providerOptions);

        // Reutiliza el contenedor de la conversación (skills y archivos ya cargados) si no se indicó uno.
        if (body["container"] is JsonObject container && container["id"] is null && FindContainerId(request.Messages) is { } containerId)
        {
            container["id"] = containerId;
        }

        return (body, betas, warnings);
    }

    internal const string ContainerMarkerType = "aisdk_container";

    internal static bool IsContainerMarker(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("type", out var type) && type.GetString() == ContainerMarkerType;

    internal static RawProviderPart ContainerMarker(string id, string? expiresAt) =>
        new("anthropic", JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["type"] = ContainerMarkerType, ["id"] = id, ["expires_at"] = expiresAt }));

    /// <summary>Último contenedor usado en la conversación (si no venció).</summary>
    internal static string? FindContainerId(IEnumerable<ModelMessage> messages)
    {
        foreach (var part in messages.Reverse().SelectMany(m => m.Content.Reverse()))
        {
            if (part is RawProviderPart { Provider: "anthropic" } raw && IsContainerMarker(raw.Data))
            {
                if (raw.Data.TryGetProperty("expires_at", out var expires) && expires.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(expires.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                    && at <= DateTimeOffset.UtcNow)
                {
                    return null;
                }

                return raw.Data.GetProperty("id").GetString();
            }
        }

        return null;
    }

    private static JsonArray ConvertMessages(IReadOnlyList<ModelMessage> messages)
    {
        var result = new JsonArray();
        JsonObject? current = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            var blocks = message.Content.Select(ConvertPart).OfType<JsonObject>().ToList();
            if (blocks.Count == 0)
            {
                continue;
            }

            // La API requiere alternancia user/assistant: se fusionan mensajes consecutivos del mismo rol.
            if (current is not null && (string?)current["role"] == role)
            {
                var content = (JsonArray)current["content"]!;
                foreach (var b in blocks)
                {
                    content.Add(b);
                }
            }
            else
            {
                current = new JsonObject { ["role"] = role, ["content"] = new JsonArray(blocks.Cast<JsonNode?>().ToArray()) };
                result.Add(current);
            }
        }

        return result;
    }

    private static JsonObject? RawBlock(ContentPart part) =>
        part.ProviderMetadata?.TryGetValue(RawBlockKey, out var raw) == true ? JsonUtil.ToNode(raw) as JsonObject : null;

    private static JsonObject? ConvertPart(ContentPart part)
    {
        switch (part)
        {
            case TextPart { Text.Length: > 0 } t:
                return new JsonObject { ["type"] = "text", ["text"] = t.Text };

            case ImagePart i:
                return new JsonObject { ["type"] = "image", ["source"] = Source(i.Data, i.Url, i.MediaType, i.FileId) };

            case FilePart { FileId: { } fileId } f:
                var fileDoc = new JsonObject { ["type"] = "document", ["source"] = new JsonObject { ["type"] = "file", ["file_id"] = fileId } };
                if (f.Filename is not null)
                {
                    fileDoc["title"] = f.Filename;
                }

                return fileDoc;

            case FilePart { MediaType: "text/plain", Data: not null } f:
                return new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = Encoding.UTF8.GetString(f.Data) },
                    ["title"] = f.Filename,
                };

            case FilePart f:
                var doc = new JsonObject { ["type"] = "document", ["source"] = Source(f.Data, f.Url, f.MediaType) };
                if (f.Filename is not null)
                {
                    doc["title"] = f.Filename;
                }

                return doc;

            case ReasoningPart r:
                // Los bloques de razonamiento deben devolverse sin cambios (con su firma).
                if (r.ProviderMetadata?.TryGetValue(RedactedDataKey, out var redacted) == true)
                {
                    return new JsonObject { ["type"] = "redacted_thinking", ["data"] = redacted.GetString() };
                }

                if (r.ProviderMetadata?.TryGetValue(SignatureKey, out var signature) == true)
                {
                    return new JsonObject { ["type"] = "thinking", ["thinking"] = r.Text, ["signature"] = signature.GetString() };
                }

                return null;

            case ToolCallPart { ProviderExecuted: true } c:
                return RawBlock(c) ?? new JsonObject { ["type"] = "server_tool_use", ["id"] = c.ToolCallId, ["name"] = c.ToolName, ["input"] = JsonUtil.ToNode(c.Input) };

            case ToolCallPart c:
                return new JsonObject { ["type"] = "tool_use", ["id"] = c.ToolCallId, ["name"] = c.ToolName, ["input"] = JsonUtil.ToNode(c.Input) };

            case ToolResultPart { ProviderExecuted: true } r:
                return RawBlock(r); // Sin el bloque original no se puede reconstruir (otro proveedor): se omite.

            case ToolResultPart r:
                var result = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = r.ToolCallId,
                    ["content"] = r.Output.ValueKind == JsonValueKind.String ? r.Output.GetString() : r.Output.GetRawText(),
                };
                if (r.IsError)
                {
                    result["is_error"] = true;
                }

                return result;

            case RawProviderPart { Provider: "anthropic" } raw when IsContainerMarker(raw.Data):
                return null; // marcador interno del contenedor: no es un bloque de la API

            case RawProviderPart { Provider: "anthropic" } raw:
                return JsonUtil.ToNode(raw.Data) as JsonObject;

            default:
                return null;
        }
    }

    private static JsonObject Source(byte[]? data, Uri? url, string mediaType, string? fileId = null) =>
        fileId is not null ? new JsonObject { ["type"] = "file", ["file_id"] = fileId }
        : data is not null ? new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(data) }
        : new JsonObject { ["type"] = "url", ["url"] = url?.ToString() };

    // ------------------------------------------------------------------
    // Respuesta
    // ------------------------------------------------------------------

    internal static LanguageModelResult ParseResponse(JsonObject json)
    {
        var content = new List<ContentPart>();
        var toolNames = new Dictionary<string, string>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in (json["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = (string?)block["type"] ?? "unknown";
            switch (type)
            {
                case "text":
                    content.Add(new TextPart((string?)block["text"] ?? string.Empty));
                    foreach (var citation in (block["citations"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (SourceFromCitation(citation, seenSources) is { } source)
                        {
                            content.Add(source);
                        }
                    }

                    break;
                case "thinking":
                    content.Add(new ReasoningPart((string?)block["thinking"] ?? string.Empty)
                    {
                        ProviderMetadata = ReasoningMetadata((string?)block["signature"], null),
                    });
                    break;
                case "redacted_thinking":
                    content.Add(new ReasoningPart(string.Empty) { ProviderMetadata = ReasoningMetadata(null, (string?)block["data"]) });
                    break;
                case "tool_use":
                    content.Add(new ToolCallPart(
                        (string?)block["id"] ?? Guid.NewGuid().ToString("N"),
                        (string?)block["name"] ?? string.Empty,
                        block["input"] is { } input ? JsonSerializer.SerializeToElement(input) : JsonUtil.EmptyObject));
                    break;
                case "server_tool_use" or "mcp_tool_use":
                    toolNames[(string?)block["id"] ?? string.Empty] = (string?)block["name"] ?? type;
                    content.Add(ServerToolCall(block));
                    break;
                default:
                    if (IsServerToolResult(type))
                    {
                        content.AddRange(ServerToolResultParts(block, toolNames, seenSources));
                    }
                    else
                    {
                        content.Add(new RawProviderPart("anthropic", JsonSerializer.SerializeToElement(block)));
                    }

                    break;
            }
        }

        if (json["container"] is JsonObject responseContainer && (string?)responseContainer["id"] is { } responseContainerId)
        {
            content.Add(ContainerMarker(responseContainerId, responseContainer["expires_at"]?.ToString()));
        }

        var stopReason = (string?)json["stop_reason"];
        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MapStopReason(stopReason),
            Usage = json["usage"] is JsonObject u ? ParseUsage(u) : Usage.Empty,
            Response = new ResponseMetadata((string?)json["id"], (string?)json["model"], DateTimeOffset.UtcNow),
            ProviderMetadata = stopReason is null ? null : new Dictionary<string, JsonElement> { [StopReasonKey] = JsonSerializer.SerializeToElement(stopReason) },
        };
    }

    private static bool IsServerToolResult(string type) => type.EndsWith("_tool_result", StringComparison.Ordinal) && type != "tool_result";

    private static ToolCallPart ServerToolCall(JsonObject block) =>
        new(
            (string?)block["id"] ?? Guid.NewGuid().ToString("N"),
            (string?)block["name"] ?? (string?)block["type"] ?? "server_tool",
            block["input"] is { } input ? JsonSerializer.SerializeToElement(input) : JsonUtil.EmptyObject)
        {
            ProviderExecuted = true,
            ProviderMetadata = new Dictionary<string, JsonElement> { [RawBlockKey] = JsonSerializer.SerializeToElement(block) },
        };

    private static IEnumerable<ContentPart> ServerToolResultParts(JsonObject block, IReadOnlyDictionary<string, string> toolNames, HashSet<string> seenSources)
    {
        var toolUseId = (string?)block["tool_use_id"] ?? string.Empty;
        var type = (string?)block["type"] ?? string.Empty;
        var name = toolNames.TryGetValue(toolUseId, out var n) ? n : type[..^"_tool_result".Length];
        var output = block["content"]?.DeepClone() ?? new JsonObject();
        var isError = output is JsonObject o && ((string?)o["type"])?.EndsWith("_error", StringComparison.Ordinal) == true;

        yield return new ToolResultPart(toolUseId, name, JsonSerializer.SerializeToElement(output))
        {
            ProviderExecuted = true,
            IsError = isError,
            ProviderMetadata = new Dictionary<string, JsonElement> { [RawBlockKey] = JsonSerializer.SerializeToElement(block) },
        };

        // Resultados de búsqueda web → fuentes.
        if (output is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>().Where(i => (string?)i["type"] == "web_search_result"))
            {
                var url = (string?)item["url"];
                if (url is not null && seenSources.Add(url))
                {
                    yield return new SourcePart($"src-{seenSources.Count}", url) { Title = (string?)item["title"] };
                }
            }
        }
        else if (output is JsonObject fetch && (string?)fetch["url"] is { } fetchedUrl && seenSources.Add(fetchedUrl))
        {
            yield return new SourcePart($"src-{seenSources.Count}", fetchedUrl) { Title = (string?)fetch["content"]?["title"] };
        }
    }

    private static SourcePart? SourceFromCitation(JsonObject citation, HashSet<string> seenSources)
    {
        var url = (string?)citation["url"];
        if (url is null || !seenSources.Add(url))
        {
            return null;
        }

        return new SourcePart($"src-{seenSources.Count}", url)
        {
            Title = (string?)citation["title"],
            CitedText = (string?)citation["cited_text"],
        };
    }

    private static Dictionary<string, JsonElement>? ReasoningMetadata(string? signature, string? redactedData)
    {
        var metadata = new Dictionary<string, JsonElement>();
        if (signature is not null)
        {
            metadata[SignatureKey] = JsonSerializer.SerializeToElement(signature);
        }

        if (redactedData is not null)
        {
            metadata[RedactedDataKey] = JsonSerializer.SerializeToElement(redactedData);
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static Usage ParseUsage(JsonObject u)
    {
        var input = (int?)u["input_tokens"];
        var cacheRead = (int?)u["cache_read_input_tokens"];
        var cacheWrite = (int?)u["cache_creation_input_tokens"];
        return new Usage
        {
            InputTokens = input is null ? null : input + (cacheRead ?? 0) + (cacheWrite ?? 0),
            OutputTokens = (int?)u["output_tokens"],
            CachedInputTokens = cacheRead,
        };
    }

    internal static FinishReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => FinishReason.Stop,
        "max_tokens" or "model_context_window_exceeded" => FinishReason.Length,
        "tool_use" => FinishReason.ToolCalls,
        "refusal" => FinishReason.ContentFilter,
        null => FinishReason.Unknown,
        _ => FinishReason.Other,
    };

    private sealed class BlockState(string type, JsonObject start)
    {
        public string Type { get; } = type;

        public JsonObject Start { get; } = start;

        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Signature { get; set; }

        public string? RedactedData { get; init; }

        public StringBuilder Json { get; } = new();
    }

    private sealed class StreamState
    {
        public int Round { get; private set; }

        public FinishReason Finish { get; set; } = FinishReason.Unknown;

        public string? RawStopReason { get; set; }

        public Usage Usage { get; set; } = Usage.Empty;

        public Dictionary<string, string> ToolNames { get; } = [];

        public HashSet<string> SeenSources { get; } = new(StringComparer.Ordinal);

        /// <summary>Contenedor ya informado (se emite un solo marcador por respuesta).</summary>
        public string? ContainerId { get; set; }

        public StreamAccumulator RoundContent { get; private set; } = new();

        public void BeginRound(int round)
        {
            Round = round;
            RawStopReason = null;
            RoundContent = new StreamAccumulator();
        }

        /// <summary>Id único por ronda y bloque, para no mezclar bloques de distintas reanudaciones.</summary>
        public string Id(string kind, int index) => Round == 0 ? $"{kind}-{index}" : $"{kind}-{Round}-{index}";
    }
}
