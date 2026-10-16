using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.Anthropic;

/// <summary>Modelo de lenguaje sobre la Messages API de Anthropic (<c>POST /v1/messages</c>).</summary>
public sealed class AnthropicMessagesLanguageModel : ILanguageModel
{
    public const string SignatureKey = "anthropic.signature";
    public const string RedactedDataKey = "anthropic.redactedData";

    private readonly AnthropicProvider _provider;

    internal AnthropicMessagesLanguageModel(string modelId, AnthropicProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, betas) = BuildBody(request, stream: false);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("messages"), body, _provider.Headers(request.Headers, betas), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(json);
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (body, betas) = BuildBody(request, stream: true);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("messages"), body, _provider.Headers(request.Headers, betas), stream: true);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: true, cancellationToken).ConfigureAwait(false);

        var blocks = new Dictionary<int, BlockState>();
        var usage = Usage.Empty;
        var finish = FinishReason.Unknown;

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
                    yield return new ResponseMetadataPart(new ResponseMetadata((string?)message?["id"], (string?)message?["model"], DateTimeOffset.UtcNow));
                    if (message?["usage"] is JsonObject startUsage)
                    {
                        usage = ParseUsage(startUsage);
                    }

                    break;

                case "content_block_start":
                {
                    var index = (int?)evt["index"] ?? 0;
                    var block = evt["content_block"] as JsonObject;
                    var state = new BlockState((string?)block?["type"] ?? "unknown")
                    {
                        Id = (string?)block?["id"],
                        Name = (string?)block?["name"],
                        RedactedData = (string?)block?["data"],
                    };
                    blocks[index] = state;
                    if (state.Type == "tool_use")
                    {
                        yield return new ToolInputStartPart(state.Id!, state.Name!);
                    }
                    else if (state.Type == "text" && (string?)block?["text"] is { Length: > 0 } initial)
                    {
                        yield return new TextDeltaPart($"text-{index}", initial);
                    }

                    break;
                }

                case "content_block_delta":
                {
                    var index = (int?)evt["index"] ?? 0;
                    var delta = evt["delta"] as JsonObject;
                    blocks.TryGetValue(index, out var state);
                    switch ((string?)delta?["type"])
                    {
                        case "text_delta":
                            yield return new TextDeltaPart($"text-{index}", (string?)delta["text"] ?? string.Empty);
                            break;
                        case "thinking_delta":
                            if ((string?)delta["thinking"] is { Length: > 0 } thinking)
                            {
                                yield return new ReasoningDeltaPart($"reasoning-{index}", thinking);
                            }

                            break;
                        case "signature_delta":
                            if (state is not null)
                            {
                                state.Signature = (string?)delta["signature"];
                            }

                            break;
                        case "input_json_delta":
                            var partial = (string?)delta["partial_json"] ?? string.Empty;
                            state?.Json.Append(partial);
                            if (state?.Id is not null && partial.Length > 0)
                            {
                                yield return new ToolInputDeltaPart(state.Id, partial);
                            }

                            break;
                    }

                    break;
                }

                case "content_block_stop":
                {
                    var index = (int?)evt["index"] ?? 0;
                    if (!blocks.TryGetValue(index, out var state))
                    {
                        break;
                    }

                    if (state.Type == "tool_use")
                    {
                        var ok = JsonUtil.TryParseElement(state.Json.ToString(), out var input);
                        yield return new ToolCallStreamPart(new ToolCallPart(state.Id!, state.Name!, input) { InvalidInput = !ok });
                    }
                    else if (state.Type is "thinking" or "redacted_thinking")
                    {
                        yield return new ReasoningEndPart($"reasoning-{index}") { ProviderMetadata = ReasoningMetadata(state.Signature, state.RedactedData) };
                    }

                    break;
                }

                case "message_delta":
                    if ((string?)evt["delta"]?["stop_reason"] is { } stop)
                    {
                        finish = MapStopReason(stop);
                    }

                    if (evt["usage"] is JsonObject deltaUsage)
                    {
                        usage = usage with { OutputTokens = (int?)deltaUsage["output_tokens"] ?? usage.OutputTokens };
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

        yield return new ModelFinishPart(finish, usage);
    }

    internal (JsonObject Body, List<string> Betas) BuildBody(LanguageModelRequest request, bool stream)
    {
        var options = _provider.Options;
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
            body["tools"] = new JsonArray(tools.Select(t =>
            {
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

                return (JsonNode?)tool;
            }).ToArray());

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
        return (body, betas);
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

    private static JsonObject? ConvertPart(ContentPart part)
    {
        switch (part)
        {
            case TextPart { Text.Length: > 0 } t:
                return new JsonObject { ["type"] = "text", ["text"] = t.Text };

            case ImagePart i:
                return new JsonObject { ["type"] = "image", ["source"] = Source(i.Data, i.Url, i.MediaType) };

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

            case ToolCallPart c:
                return new JsonObject { ["type"] = "tool_use", ["id"] = c.ToolCallId, ["name"] = c.ToolName, ["input"] = JsonUtil.ToNode(c.Input) };

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

            default:
                return null;
        }
    }

    private static JsonObject Source(byte[]? data, Uri? url, string mediaType) => data is not null
        ? new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(data) }
        : new JsonObject { ["type"] = "url", ["url"] = url?.ToString() };

    internal static LanguageModelResult ParseResponse(JsonObject json)
    {
        var content = new List<ContentPart>();
        foreach (var block in (json["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch ((string?)block["type"])
            {
                case "text":
                    content.Add(new TextPart((string?)block["text"] ?? string.Empty));
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
            }
        }

        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MapStopReason((string?)json["stop_reason"]),
            Usage = json["usage"] is JsonObject u ? ParseUsage(u) : Usage.Empty,
            Response = new ResponseMetadata((string?)json["id"], (string?)json["model"], DateTimeOffset.UtcNow),
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

    private sealed class BlockState(string type)
    {
        public string Type { get; } = type;

        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Signature { get; set; }

        public string? RedactedData { get; init; }

        public StringBuilder Json { get; } = new();
    }
}
