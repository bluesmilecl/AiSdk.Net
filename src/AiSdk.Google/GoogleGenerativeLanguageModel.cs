using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.Google;

/// <summary>Gemini model over <c>generateContent</c> / <c>streamGenerateContent</c>.</summary>
public sealed class GoogleGenerativeLanguageModel : ILanguageModel, ITokenCountingModel
{
    /// <summary>Thought signature that Gemini requires to be sent back along with function calls.</summary>
    public const string ThoughtSignatureKey = "google.thoughtSignature";

    /// <summary>Original part (code execution), forwarded as is.</summary>
    public const string RawPartKey = "google.rawPart";

    private readonly GoogleProvider _provider;

    internal GoogleGenerativeLanguageModel(string modelId, GoogleProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, warnings) = BuildBody(request);
        using var http = ProviderHttp.CreateJsonRequest(_provider.ModelUrl(ModelId, "generateContent"), body, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(json) with { Warnings = warnings };
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (body, _) = BuildBody(request);
        using var http = ProviderHttp.CreateJsonRequest(_provider.ModelUrl(ModelId, "streamGenerateContent", "alt=sse"), body, _provider.Headers(request.Headers), stream: true);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: true, cancellationToken).ConfigureAwait(false);

        var finish = FinishReason.Unknown;
        var usage = Usage.Empty;
        var hasFunctionCalls = false;
        var sentMetadata = false;
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        string? lastCodeId = null;
        var reasoningIndex = 0;
        var inReasoning = false;

        await foreach (var sse in ProviderHttp.ReadSseAsync(response, ProviderHttp.ModelStreamOptions, cancellationToken).ConfigureAwait(false))
        {
            if (ProviderHttp.ParseSseJson(sse, "Google") is not { } chunk)
            {
                continue;
            }

            if (chunk["error"] is JsonObject error)
            {
                yield return new ErrorPart(new AiSdkException((string?)error["message"] ?? "Error en el stream de Gemini."));
                yield break;
            }

            if (!sentMetadata)
            {
                sentMetadata = true;
                yield return new ResponseMetadataPart(new ResponseMetadata((string?)chunk["responseId"], (string?)chunk["modelVersion"], DateTimeOffset.UtcNow));
            }

            if (chunk["usageMetadata"] is JsonObject u)
            {
                usage = ParseUsage(u);
            }

            if (chunk["candidates"]?[0] is not JsonObject candidate)
            {
                if ((string?)chunk["promptFeedback"]?["blockReason"] is not null)
                {
                    finish = FinishReason.ContentFilter;
                }

                continue;
            }

            foreach (var part in (candidate["content"]?["parts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var signature = (string?)part["thoughtSignature"];
                if ((bool?)part["thought"] == true)
                {
                    inReasoning = true;
                    if ((string?)part["text"] is { Length: > 0 } thought)
                    {
                        yield return new ReasoningDeltaPart($"reasoning-{reasoningIndex}", thought);
                    }

                    if (signature is not null)
                    {
                        yield return new ReasoningEndPart($"reasoning-{reasoningIndex}") { ProviderMetadata = Signature(signature) };
                        reasoningIndex++;
                        inReasoning = false;
                    }

                    continue;
                }

                if (inReasoning)
                {
                    yield return new ReasoningEndPart($"reasoning-{reasoningIndex}");
                    reasoningIndex++;
                    inReasoning = false;
                }

                if (part["functionCall"] is JsonObject call)
                {
                    hasFunctionCalls = true;
                    var toolCall = FunctionCall(call, signature);
                    yield return new ToolInputStartPart(toolCall.ToolCallId, toolCall.ToolName);
                    yield return new ToolCallStreamPart(toolCall);
                }
                else if (part["executableCode"] is not null)
                {
                    var codeCall = CodeCall(part);
                    lastCodeId = codeCall.ToolCallId;
                    yield return new ToolCallStreamPart(codeCall);
                }
                else if (part["codeExecutionResult"] is not null)
                {
                    yield return new ToolResultStreamPart(CodeResult(part, lastCodeId));
                }
                else if ((string?)part["text"] is { Length: > 0 } text)
                {
                    yield return new TextDeltaPart("text-0", text);
                }
            }

            foreach (var source in Sources(candidate, seenSources))
            {
                yield return new SourceStreamPart(source);
            }

            if ((string?)candidate["finishReason"] is { } reason)
            {
                finish = MapFinish(reason, hasFunctionCalls);
            }
        }

        if (inReasoning)
        {
            yield return new ReasoningEndPart($"reasoning-{reasoningIndex}");
        }

        yield return new ModelFinishPart(finish, usage);
    }

    /// <summary>Counts tokens with <c>countTokens</c>.</summary>
    public async Task<int> CountTokensAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, _) = BuildBody(request);
        var generateRequest = new JsonObject { ["model"] = GoogleProvider.ModelName(ModelId) };
        foreach (var key in new[] { "contents", "systemInstruction", "tools", "toolConfig" })
        {
            if (body[key] is { } value)
            {
                generateRequest[key] = value.DeepClone();
            }
        }

        using var http = ProviderHttp.CreateJsonRequest(
            _provider.ModelUrl(ModelId, "countTokens"),
            new JsonObject { ["generateContentRequest"] = generateRequest },
            _provider.Headers(request.Headers),
            stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return (int?)json["totalTokens"] ?? 0;
    }

    // ------------------------------------------------------------------
    // Petición
    // ------------------------------------------------------------------

    internal static (JsonObject Body, List<string> Warnings) BuildBody(LanguageModelRequest request)
    {
        var warnings = new List<string>();
        var body = new JsonObject { ["contents"] = ConvertContents(request.Messages) };

        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        if (system.Length > 0)
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };
        }

        var generation = new JsonObject();
        if (request.MaxOutputTokens is { } max)
        {
            generation["maxOutputTokens"] = max;
        }

        if (request.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            generation["topP"] = topP;
        }

        if (request.TopK is { } topK)
        {
            generation["topK"] = topK;
        }

        if (request.Seed is { } seed)
        {
            generation["seed"] = seed;
        }

        if (request.PresencePenalty is { } presence)
        {
            generation["presencePenalty"] = presence;
        }

        if (request.FrequencyPenalty is { } frequency)
        {
            generation["frequencyPenalty"] = frequency;
        }

        if (request.StopSequences is { Count: > 0 } stop)
        {
            generation["stopSequences"] = new JsonArray(stop.Select(s => (JsonNode?)s).ToArray());
        }

        if (request.ResponseFormat is { Type: ResponseFormatType.Json } format)
        {
            generation["responseMimeType"] = "application/json";
            if (format.Schema is { } schema)
            {
                generation["responseJsonSchema"] = JsonUtil.ToNode(schema);
            }
        }

        if (generation.Count > 0)
        {
            body["generationConfig"] = generation;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            var toolArray = new JsonArray();
            var functions = new JsonArray();
            foreach (var t in tools)
            {
                if (t.IsProviderTool)
                {
                    if (string.Equals(t.ProviderName, "google", StringComparison.OrdinalIgnoreCase))
                    {
                        toolArray.Add(t.ProviderDefinition!.DeepClone());
                    }
                    else
                    {
                        warnings.Add($"La herramienta '{t.Name}' es de '{t.ProviderName}' y se omitió en Gemini.");
                    }

                    continue;
                }

                var fn = new JsonObject { ["name"] = t.Name, ["parametersJsonSchema"] = JsonUtil.ToNode(t.InputSchema) };
                if (t.Description is not null)
                {
                    fn["description"] = t.Description;
                }

                functions.Add(fn);
            }

            if (functions.Count > 0)
            {
                toolArray.Insert(0, new JsonObject { ["functionDeclarations"] = functions });
                if (request.ToolChoice is { } choice)
                {
                    var config = new JsonObject
                    {
                        ["mode"] = choice.Kind switch
                        {
                            ToolChoiceKind.None => "NONE",
                            ToolChoiceKind.Required or ToolChoiceKind.Tool => "ANY",
                            _ => "AUTO",
                        },
                    };
                    if (choice.Kind == ToolChoiceKind.Tool)
                    {
                        config["allowedFunctionNames"] = new JsonArray(choice.ToolName);
                    }

                    body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
                }
            }

            if (toolArray.Count > 0)
            {
                body["tools"] = toolArray;
            }
        }

        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("google"));
        return (body, warnings);
    }

    private static JsonArray ConvertContents(IReadOnlyList<ModelMessage> messages)
    {
        var contents = new JsonArray();
        JsonObject? current = null;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "model" : "user";
            var parts = message.Content.Select(ConvertPart).OfType<JsonObject>().ToList();
            if (parts.Count == 0)
            {
                continue;
            }

            if (current is not null && (string?)current["role"] == role)
            {
                var existing = (JsonArray)current["parts"]!;
                parts.ForEach(existing.Add);
            }
            else
            {
                current = new JsonObject { ["role"] = role, ["parts"] = new JsonArray(parts.Cast<JsonNode?>().ToArray()) };
                contents.Add(current);
            }
        }

        return contents;
    }

    private static string? Meta(ContentPart part, string key) =>
        part.ProviderMetadata?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonObject WithSignature(JsonObject part, ContentPart source)
    {
        if (Meta(source, ThoughtSignatureKey) is { } signature)
        {
            part["thoughtSignature"] = signature;
        }

        return part;
    }

    private static JsonObject? ConvertPart(ContentPart part)
    {
        switch (part)
        {
            case TextPart { Text.Length: > 0 } t:
                return WithSignature(new JsonObject { ["text"] = t.Text }, t);

            case ReasoningPart r when Meta(r, ThoughtSignatureKey) is not null:
                return WithSignature(new JsonObject { ["text"] = r.Text, ["thought"] = true }, r);

            case ImagePart { Data: not null } i:
                return new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = i.MediaType, ["data"] = Convert.ToBase64String(i.Data) } };

            case ImagePart i when (i.FileId ?? i.Url?.ToString()) is { } uri:
                return new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = i.MediaType, ["fileUri"] = uri } };

            case FilePart { Data: not null } f:
                return new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = f.MediaType, ["data"] = Convert.ToBase64String(f.Data) } };

            case FilePart f when (f.FileId ?? f.Url?.ToString()) is { } fileUri:
                return new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = f.MediaType, ["fileUri"] = fileUri } };

            case ToolCallPart { ProviderExecuted: true } or ToolResultPart { ProviderExecuted: true }:
                return part.ProviderMetadata?.TryGetValue(RawPartKey, out var raw) == true ? JsonUtil.ToNode(raw) as JsonObject : null;

            case ToolCallPart c:
                return WithSignature(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = c.ToolName, ["args"] = JsonUtil.ToNode(c.Input) } }, c);

            case ToolResultPart r:
                return new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = r.ToolName,
                        ["response"] = new JsonObject { ["name"] = r.ToolName, ["content"] = JsonUtil.ToNode(r.Output) },
                    },
                };

            case RawProviderPart { Provider: "google" } rawPart:
                return JsonUtil.ToNode(rawPart.Data) as JsonObject;

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------
    // Respuesta
    // ------------------------------------------------------------------

    internal static LanguageModelResult ParseResponse(JsonObject json)
    {
        var content = new List<ContentPart>();
        var hasFunctionCalls = false;
        var finish = FinishReason.Unknown;
        string? lastCodeId = null;

        if (json["candidates"]?[0] is JsonObject candidate)
        {
            foreach (var part in (candidate["content"]?["parts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var signature = (string?)part["thoughtSignature"];
                if ((bool?)part["thought"] == true)
                {
                    content.Add(new ReasoningPart((string?)part["text"] ?? string.Empty) { ProviderMetadata = Signature(signature) });
                }
                else if (part["functionCall"] is JsonObject call)
                {
                    hasFunctionCalls = true;
                    content.Add(FunctionCall(call, signature));
                }
                else if (part["executableCode"] is not null)
                {
                    var codeCall = CodeCall(part);
                    lastCodeId = codeCall.ToolCallId;
                    content.Add(codeCall);
                }
                else if (part["codeExecutionResult"] is not null)
                {
                    content.Add(CodeResult(part, lastCodeId));
                }
                else if ((string?)part["text"] is { } text)
                {
                    content.Add(new TextPart(text) { ProviderMetadata = Signature(signature) });
                }
            }

            content.AddRange(Sources(candidate, new HashSet<string>(StringComparer.Ordinal)));
            finish = MapFinish((string?)candidate["finishReason"], hasFunctionCalls);
        }
        else if (json["promptFeedback"]?["blockReason"] is not null)
        {
            finish = FinishReason.ContentFilter;
        }

        return new LanguageModelResult
        {
            Content = content,
            FinishReason = finish,
            Usage = json["usageMetadata"] is JsonObject u ? ParseUsage(u) : Usage.Empty,
            Response = new ResponseMetadata((string?)json["responseId"], (string?)json["modelVersion"], DateTimeOffset.UtcNow),
        };
    }

    private static Dictionary<string, JsonElement>? Signature(string? signature) =>
        signature is null ? null : new Dictionary<string, JsonElement> { [ThoughtSignatureKey] = JsonSerializer.SerializeToElement(signature) };

    private static ToolCallPart FunctionCall(JsonObject call, string? signature) =>
        new(
            (string?)call["id"] ?? $"call_{Guid.NewGuid():N}",
            (string?)call["name"] ?? string.Empty,
            call["args"] is { } args ? JsonSerializer.SerializeToElement(args) : JsonUtil.EmptyObject)
        {
            ProviderMetadata = Signature(signature),
        };

    private static ToolCallPart CodeCall(JsonObject part) =>
        new($"code_{Guid.NewGuid():N}", "code_execution", JsonSerializer.SerializeToElement(part["executableCode"]))
        {
            ProviderExecuted = true,
            ProviderMetadata = new Dictionary<string, JsonElement> { [RawPartKey] = JsonSerializer.SerializeToElement(part) },
        };

    private static ToolResultPart CodeResult(JsonObject part, string? callId)
    {
        var result = part["codeExecutionResult"] as JsonObject ?? [];
        return new ToolResultPart(callId ?? $"code_{Guid.NewGuid():N}", "code_execution", JsonSerializer.SerializeToElement(result))
        {
            ProviderExecuted = true,
            IsError = (string?)result["outcome"] is { } outcome && outcome != "OUTCOME_OK",
            ProviderMetadata = new Dictionary<string, JsonElement> { [RawPartKey] = JsonSerializer.SerializeToElement(part) },
        };
    }

    private static IEnumerable<SourcePart> Sources(JsonObject candidate, HashSet<string> seen)
    {
        foreach (var chunk in (candidate["groundingMetadata"]?["groundingChunks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var web = chunk["web"] as JsonObject ?? chunk["retrievedContext"] as JsonObject;
            if ((string?)web?["uri"] is { } uri && seen.Add(uri))
            {
                yield return new SourcePart($"src-{seen.Count}", uri) { Title = (string?)web!["title"] };
            }
        }
    }

    private static Usage ParseUsage(JsonObject u)
    {
        var candidates = (int?)u["candidatesTokenCount"];
        var thoughts = (int?)u["thoughtsTokenCount"];
        return new Usage
        {
            InputTokens = (int?)u["promptTokenCount"],
            OutputTokens = candidates is null && thoughts is null ? null : (candidates ?? 0) + (thoughts ?? 0),
            ReasoningTokens = thoughts,
            CachedInputTokens = (int?)u["cachedContentTokenCount"],
        };
    }

    private static FinishReason MapFinish(string? reason, bool hasFunctionCalls) => reason switch
    {
        "STOP" => hasFunctionCalls ? FinishReason.ToolCalls : FinishReason.Stop,
        "MAX_TOKENS" => FinishReason.Length,
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" => FinishReason.ContentFilter,
        "MALFORMED_FUNCTION_CALL" => FinishReason.Error,
        null => hasFunctionCalls ? FinishReason.ToolCalls : FinishReason.Unknown,
        _ => FinishReason.Other,
    };
}
