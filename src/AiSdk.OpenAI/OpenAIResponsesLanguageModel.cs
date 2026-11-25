using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.OpenAI;

/// <summary>
/// Language model over the OpenAI Responses API (<c>POST /responses</c>), the API
/// recommended by OpenAI: function and server tools (web search, code
/// interpreter, file search), reasoning with summaries and structured output.
/// </summary>
public sealed class OpenAIResponsesLanguageModel : ILanguageModel, ITokenCountingModel
{
    /// <summary>Id of the output item (to forward reasoning and calls with <c>item_reference</c>).</summary>
    public const string ItemIdKey = "openai.itemId";

    /// <summary>Encrypted reasoning (when <c>store: false</c> is used with <c>include: ["reasoning.encrypted_content"]</c>).</summary>
    public const string EncryptedContentKey = "openai.encryptedContent";

    private static readonly HashSet<string> ProviderToolItems =
        ["web_search_call", "code_interpreter_call", "file_search_call", "image_generation_call", "computer_call", "local_shell_call", "mcp_call"];

    private readonly OpenAIProvider _provider;

    internal OpenAIResponsesLanguageModel(string modelId, OpenAIProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, warnings) = BuildBody(request, stream: false);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("responses"), body, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if ((string?)json["status"] == "failed")
        {
            throw new AiSdkException((string?)json["error"]?["message"] ?? "La respuesta de OpenAI falló.");
        }

        return ParseResponse(json) with { Warnings = warnings };
    }

    /// <summary>Counts the input tokens with <c>/responses/input_tokens</c>.</summary>
    public async Task<int> CountTokensAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, _) = BuildBody(request, stream: false);
        var countBody = new JsonObject();
        foreach (var key in new[] { "model", "input", "instructions", "tools", "tool_choice", "text", "reasoning", "parallel_tool_calls" })
        {
            if (body[key] is { } value)
            {
                countBody[key] = value.DeepClone();
            }
        }

        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("responses/input_tokens"), countBody, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return (int?)json["input_tokens"] ?? 0;
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (body, _) = BuildBody(request, stream: true);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("responses"), body, _provider.Headers(request.Headers), stream: true);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: true, cancellationToken).ConfigureAwait(false);

        var callIds = new Dictionary<string, string>(); // item_id → call_id
        var reasoningSummaryIndex = new Dictionary<string, int>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var hasFunctionCalls = false;
        var finish = FinishReason.Unknown;
        var usage = Usage.Empty;

        await foreach (var sse in ProviderHttp.ReadSseAsync(response, ProviderHttp.ModelStreamOptions, cancellationToken).ConfigureAwait(false))
        {
            if (ProviderHttp.ParseSseJson(sse, "OpenAI") is not { } evt)
            {
                continue; // [DONE], keep-alive o último evento cortado
            }

            switch ((string?)evt["type"])
            {
                case "response.created":
                    yield return new ResponseMetadataPart(Metadata(evt["response"] as JsonObject ?? []));
                    break;

                case "response.output_item.added":
                    if (evt["item"] is JsonObject { } added && (string?)added["type"] == "function_call")
                    {
                        var callId = (string?)added["call_id"] ?? (string?)added["id"] ?? Guid.NewGuid().ToString("N");
                        callIds[(string?)added["id"] ?? callId] = callId;
                        yield return new ToolInputStartPart(callId, (string?)added["name"] ?? string.Empty);
                    }

                    break;

                case "response.output_text.delta":
                    yield return new TextDeltaPart($"{(string?)evt["item_id"]}:{(int?)evt["content_index"] ?? 0}", (string?)evt["delta"] ?? string.Empty);
                    break;

                case "response.output_text.annotation.added":
                    if (evt["annotation"] is JsonObject annotation && SourceFromAnnotation(annotation, seenSources) is { } source)
                    {
                        yield return new SourceStreamPart(source);
                    }

                    break;

                case "response.reasoning_summary_text.delta":
                {
                    var itemId = (string?)evt["item_id"] ?? "reasoning";
                    var summaryIndex = (int?)evt["summary_index"] ?? 0;
                    var delta = (string?)evt["delta"] ?? string.Empty;
                    if (reasoningSummaryIndex.TryGetValue(itemId, out var previous) && previous != summaryIndex)
                    {
                        delta = "\n\n" + delta;
                    }

                    reasoningSummaryIndex[itemId] = summaryIndex;
                    yield return new ReasoningDeltaPart(itemId, delta);
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    var itemId = (string?)evt["item_id"] ?? string.Empty;
                    if (callIds.TryGetValue(itemId, out var callId))
                    {
                        yield return new ToolInputDeltaPart(callId, (string?)evt["delta"] ?? string.Empty);
                    }

                    break;
                }

                case "response.output_item.done":
                    if (evt["item"] is not JsonObject item)
                    {
                        break;
                    }

                    var itemType = (string?)item["type"];
                    if (itemType == "function_call")
                    {
                        hasFunctionCalls = true;
                        yield return new ToolCallStreamPart(FunctionCall(item));
                    }
                    else if (itemType == "reasoning")
                    {
                        yield return new ReasoningEndPart((string?)item["id"] ?? "reasoning") { ProviderMetadata = ReasoningMetadata(item) };
                    }
                    else if (itemType is not null && ProviderToolItems.Contains(itemType))
                    {
                        var (call, result) = ProviderToolParts(item);
                        yield return new ToolCallStreamPart(call);
                        yield return new ToolResultStreamPart(result);
                    }
                    else if (itemType != "message")
                    {
                        yield return new ContentStreamPart(new RawProviderPart("openai", JsonSerializer.SerializeToElement(item)));
                    }

                    break;

                case "response.completed":
                case "response.incomplete":
                    var final = evt["response"] as JsonObject ?? [];
                    usage = final["usage"] is JsonObject u ? ParseUsage(u) : usage;
                    finish = MapFinish(final, hasFunctionCalls);
                    break;

                case "response.failed":
                    yield return new ErrorPart(new AiSdkException((string?)evt["response"]?["error"]?["message"] ?? "La respuesta de OpenAI falló."));
                    yield break;

                case "error":
                    yield return new ErrorPart(new AiSdkException((string?)evt["message"] ?? (string?)evt["error"]?["message"] ?? "Error en el stream de OpenAI."));
                    yield break;
            }
        }

        yield return new ModelFinishPart(finish, usage);
    }

    // ------------------------------------------------------------------
    // Petición
    // ------------------------------------------------------------------

    internal (JsonObject Body, List<string> Warnings) BuildBody(LanguageModelRequest request, bool stream)
    {
        var warnings = new List<string>();
        // Con store: false OpenAI no guarda los items, así que no se pueden referenciar por id.
        var store = request.ProviderOptions?.For("openai")?["store"] is not JsonValue storeValue
                    || !storeValue.TryGetValue<bool>(out var storeFlag)
                    || storeFlag;
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["input"] = ConvertInput(request.Messages, store),
        };

        if (request.MaxOutputTokens is { } max)
        {
            body["max_output_tokens"] = max;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.StopSequences is { Count: > 0 })
        {
            warnings.Add("La API Responses no admite StopSequences; se ignoraron.");
        }

        if (request.Seed is not null || request.TopK is not null || request.PresencePenalty is not null || request.FrequencyPenalty is not null)
        {
            warnings.Add("La API Responses no admite Seed, TopK ni penalizaciones; se ignoraron.");
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            var array = new JsonArray();
            foreach (var t in tools)
            {
                if (t.IsProviderTool)
                {
                    if (string.Equals(t.ProviderName, "openai", StringComparison.OrdinalIgnoreCase))
                    {
                        array.Add(t.ProviderDefinition!.DeepClone());
                    }
                    else
                    {
                        warnings.Add($"La herramienta '{t.Name}' es de '{t.ProviderName}' y se omitió en OpenAI.");
                    }

                    continue;
                }

                var fn = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["parameters"] = JsonUtil.ToNode(t.Strict ? JsonSchema.ToStrict(t.InputSchema) : t.InputSchema),
                    ["strict"] = t.Strict,
                };
                if (t.Description is not null)
                {
                    fn["description"] = t.Description;
                }

                array.Add(fn);
            }

            if (array.Count > 0)
            {
                body["tools"] = array;
                if (request.ToolChoice is { } choice)
                {
                    body["tool_choice"] = choice.Kind switch
                    {
                        ToolChoiceKind.Auto => "auto",
                        ToolChoiceKind.None => "none",
                        ToolChoiceKind.Required => "required",
                        _ => new JsonObject { ["type"] = "function", ["name"] = choice.ToolName },
                    };
                }
            }
        }

        if (request.ResponseFormat is { Type: ResponseFormatType.Json } format)
        {
            var formatNode = format.Schema is { } schema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = format.Name ?? "response",
                    ["schema"] = JsonUtil.ToNode(JsonSchema.ToStrict(schema)),
                    ["strict"] = true,
                }
                : new JsonObject { ["type"] = "json_object" };
            if (format.Description is not null && formatNode["type"]?.GetValue<string>() == "json_schema")
            {
                formatNode["description"] = format.Description;
            }

            body["text"] = new JsonObject { ["format"] = formatNode };
        }

        if (stream)
        {
            body["stream"] = true;
        }

        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("openai"));
        if (!string.Equals(Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            JsonUtil.DeepMerge(body, request.ProviderOptions?.For(Provider));
        }

        return (body, warnings);
    }

    private static JsonArray ConvertInput(IReadOnlyList<ModelMessage> messages, bool store)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    input.Add(new JsonObject { ["role"] = "system", ["content"] = message.Text });
                    break;

                case ChatRole.User:
                    var content = new JsonArray();
                    foreach (var part in message.Content)
                    {
                        JsonObject? converted = part switch
                        {
                            TextPart t => new JsonObject { ["type"] = "input_text", ["text"] = t.Text },
                            ImagePart { FileId: { } imageFileId } => new JsonObject { ["type"] = "input_image", ["file_id"] = imageFileId },
                            ImagePart i => new JsonObject { ["type"] = "input_image", ["image_url"] = i.Url?.ToString() ?? ProviderHttp.ToDataUrl(i.Data!, i.MediaType) },
                            FilePart { FileId: { } fileId } => new JsonObject { ["type"] = "input_file", ["file_id"] = fileId },
                            FilePart { Url: not null } f => new JsonObject { ["type"] = "input_file", ["file_url"] = f.Url.ToString() },
                            FilePart f => new JsonObject
                            {
                                ["type"] = "input_file",
                                ["filename"] = f.Filename ?? "file",
                                ["file_data"] = ProviderHttp.ToDataUrl(f.Data!, f.MediaType),
                            },
                            _ => null,
                        };
                        if (converted is not null)
                        {
                            content.Add(converted);
                        }
                    }

                    input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;

                case ChatRole.Assistant:
                    foreach (var part in message.Content)
                    {
                        switch (part)
                        {
                            case TextPart { Text.Length: > 0 } t:
                                input.Add(new JsonObject { ["role"] = "assistant", ["content"] = t.Text });
                                break;
                            case ReasoningPart r when Meta(r, ItemIdKey) is { } reasoningId:
                                if (Meta(r, EncryptedContentKey) is { } encrypted)
                                {
                                    input.Add(new JsonObject
                                    {
                                        ["type"] = "reasoning",
                                        ["id"] = reasoningId,
                                        ["encrypted_content"] = encrypted,
                                        ["summary"] = r.Text.Length > 0
                                            ? new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = r.Text })
                                            : new JsonArray(),
                                    });
                                }
                                else if (store)
                                {
                                    input.Add(new JsonObject { ["type"] = "item_reference", ["id"] = reasoningId });
                                }

                                break;
                            case ToolCallPart { ProviderExecuted: true } c when store && Meta(c, ItemIdKey) is { } itemId:
                                input.Add(new JsonObject { ["type"] = "item_reference", ["id"] = itemId });
                                break;
                            case ToolCallPart { ProviderExecuted: false } c:
                                input.Add(new JsonObject
                                {
                                    ["type"] = "function_call",
                                    ["call_id"] = c.ToolCallId,
                                    ["name"] = c.ToolName,
                                    ["arguments"] = c.Input.GetRawText(),
                                });
                                break;
                            case RawProviderPart { Provider: "openai" } raw:
                                input.Add(JsonUtil.ToNode(raw.Data));
                                break;
                        }
                    }

                    break;

                case ChatRole.Tool:
                    foreach (var r in message.Content.OfType<ToolResultPart>().Where(r => !r.ProviderExecuted))
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = r.ToolCallId,
                            ["output"] = r.Output.ValueKind == JsonValueKind.String ? r.Output.GetString() : r.Output.GetRawText(),
                        });
                    }

                    break;
            }
        }

        return input;
    }

    private static string? Meta(ContentPart part, string key) =>
        part.ProviderMetadata?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ------------------------------------------------------------------
    // Respuesta
    // ------------------------------------------------------------------

    internal static LanguageModelResult ParseResponse(JsonObject json)
    {
        var content = new List<ContentPart>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var hasFunctionCalls = false;
        foreach (var item in (json["output"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = (string?)item["type"];
            switch (type)
            {
                case "reasoning":
                    var summary = string.Join("\n\n", (item["summary"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (string?)s["text"]));
                    content.Add(new ReasoningPart(summary) { ProviderMetadata = ReasoningMetadata(item) });
                    break;

                case "message":
                    foreach (var block in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        switch ((string?)block["type"])
                        {
                            case "output_text":
                                content.Add(new TextPart((string?)block["text"] ?? string.Empty));
                                foreach (var annotation in (block["annotations"] as JsonArray ?? []).OfType<JsonObject>())
                                {
                                    if (SourceFromAnnotation(annotation, seenSources) is { } source)
                                    {
                                        content.Add(source);
                                    }
                                }

                                break;
                            case "refusal":
                                content.Add(new TextPart((string?)block["refusal"] ?? string.Empty));
                                break;
                        }
                    }

                    break;

                case "function_call":
                    hasFunctionCalls = true;
                    content.Add(FunctionCall(item));
                    break;

                default:
                    if (type is not null && ProviderToolItems.Contains(type))
                    {
                        var (call, result) = ProviderToolParts(item);
                        content.Add(call);
                        content.Add(result);
                    }
                    else
                    {
                        content.Add(new RawProviderPart("openai", JsonSerializer.SerializeToElement(item)));
                    }

                    break;
            }
        }

        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MapFinish(json, hasFunctionCalls),
            Usage = json["usage"] is JsonObject u ? ParseUsage(u) : Usage.Empty,
            Response = Metadata(json),
        };
    }

    private static ToolCallPart FunctionCall(JsonObject item)
    {
        var ok = JsonUtil.TryParseElement((string?)item["arguments"], out var input);
        return new ToolCallPart(
            (string?)item["call_id"] ?? (string?)item["id"] ?? Guid.NewGuid().ToString("N"),
            (string?)item["name"] ?? string.Empty,
            input)
        {
            InvalidInput = !ok,
            ProviderMetadata = ItemMetadata(item),
        };
    }

    private static (ToolCallPart Call, ToolResultPart Result) ProviderToolParts(JsonObject item)
    {
        var type = (string?)item["type"] ?? "unknown_call";
        var id = (string?)item["id"] ?? Guid.NewGuid().ToString("N");
        var name = type[..^"_call".Length];
        JsonNode input = type switch
        {
            "web_search_call" => item["action"]?.DeepClone() ?? new JsonObject(),
            "code_interpreter_call" => new JsonObject { ["code"] = item["code"]?.DeepClone(), ["container_id"] = item["container_id"]?.DeepClone() },
            "file_search_call" => new JsonObject { ["queries"] = item["queries"]?.DeepClone() },
            _ => new JsonObject(),
        };
        JsonNode output = type switch
        {
            "web_search_call" => new JsonObject { ["status"] = item["status"]?.DeepClone() },
            "code_interpreter_call" => new JsonObject { ["outputs"] = item["outputs"]?.DeepClone(), ["status"] = item["status"]?.DeepClone() },
            "file_search_call" => new JsonObject { ["results"] = item["results"]?.DeepClone(), ["status"] = item["status"]?.DeepClone() },
            _ => item.DeepClone(),
        };
        var metadata = ItemMetadata(item);
        return (
            new ToolCallPart(id, name, JsonSerializer.SerializeToElement(input)) { ProviderExecuted = true, ProviderMetadata = metadata },
            new ToolResultPart(id, name, JsonSerializer.SerializeToElement(output))
            {
                ProviderExecuted = true,
                IsError = (string?)item["status"] == "failed",
                ProviderMetadata = metadata,
            });
    }

    private static Dictionary<string, JsonElement>? ItemMetadata(JsonObject item) =>
        (string?)item["id"] is { } id ? new Dictionary<string, JsonElement> { [ItemIdKey] = JsonSerializer.SerializeToElement(id) } : null;

    private static Dictionary<string, JsonElement>? ReasoningMetadata(JsonObject item)
    {
        var metadata = ItemMetadata(item) ?? [];
        if ((string?)item["encrypted_content"] is { } encrypted)
        {
            metadata[EncryptedContentKey] = JsonSerializer.SerializeToElement(encrypted);
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static SourcePart? SourceFromAnnotation(JsonObject annotation, HashSet<string> seen)
    {
        if ((string?)annotation["type"] != "url_citation" || (string?)annotation["url"] is not { } url || !seen.Add(url))
        {
            return null;
        }

        return new SourcePart($"src-{seen.Count}", url) { Title = (string?)annotation["title"] };
    }

    private static FinishReason MapFinish(JsonObject response, bool hasFunctionCalls) =>
        (string?)response["status"] switch
        {
            "incomplete" => (string?)response["incomplete_details"]?["reason"] switch
            {
                "max_output_tokens" => FinishReason.Length,
                "content_filter" => FinishReason.ContentFilter,
                _ => FinishReason.Other,
            },
            "failed" => FinishReason.Error,
            "completed" => hasFunctionCalls ? FinishReason.ToolCalls : FinishReason.Stop,
            null => hasFunctionCalls ? FinishReason.ToolCalls : FinishReason.Unknown,
            _ => FinishReason.Other,
        };

    private static ResponseMetadata Metadata(JsonObject json) => new(
        (string?)json["id"],
        (string?)json["model"],
        json["created_at"] is JsonValue created && created.TryGetValue<long>(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow);

    private static Usage ParseUsage(JsonObject u) => new()
    {
        InputTokens = (int?)u["input_tokens"],
        OutputTokens = (int?)u["output_tokens"],
        ReasoningTokens = (int?)u["output_tokens_details"]?["reasoning_tokens"],
        CachedInputTokens = (int?)u["input_tokens_details"]?["cached_tokens"],
    };
}
