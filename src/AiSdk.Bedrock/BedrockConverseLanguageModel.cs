using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.Bedrock;

/// <summary>Modelo sobre la API Converse de Bedrock (<c>/converse</c> y <c>/converse-stream</c>).</summary>
public sealed class BedrockConverseLanguageModel : ILanguageModel
{
    public const string SignatureKey = "bedrock.signature";
    public const string RedactedContentKey = "bedrock.redactedContent";

    private readonly BedrockProvider _provider;

    internal BedrockConverseLanguageModel(string modelId, BedrockProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var (body, warnings) = BuildBody(request);
        using var http = await _provider.CreateRequestAsync(_provider.ModelUrl(ModelId, "converse"), body, stream: false, cancellationToken).ConfigureAwait(false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(json) with { Warnings = warnings };
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (body, _) = BuildBody(request);
        using var http = await _provider.CreateRequestAsync(_provider.ModelUrl(ModelId, "converse-stream"), body, stream: true, cancellationToken).ConfigureAwait(false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: true, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var tools = new Dictionary<int, (string Id, string Name, StringBuilder Input)>();
        var signatures = new Dictionary<int, string>();
        var redacted = new Dictionary<int, string>();
        var reasoningBlocks = new HashSet<int>();
        var finish = FinishReason.Unknown;
        var usage = Usage.Empty;

        await foreach (var message in AwsEventStream.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (message.MessageType == "exception" || message.MessageType == "error")
            {
                var text = message.PayloadText;
                var detail = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text;
                var type = message.ExceptionType ?? message.Headers.GetValueOrDefault(":error-code") ?? "error";
                yield return new ErrorPart(new ApiCallException($"{type}: {detail}", type.Contains("throttling", StringComparison.OrdinalIgnoreCase) ? 429 : 500, text, http.RequestUri));
                yield break;
            }

            if (JsonNode.Parse(message.Payload) is not JsonObject evt)
            {
                continue;
            }

            var index = (int?)evt["contentBlockIndex"] ?? 0;
            switch (message.EventType)
            {
                case "contentBlockStart":
                    if (evt["start"]?["toolUse"] is JsonObject start)
                    {
                        var entry = ((string?)start["toolUseId"] ?? Guid.NewGuid().ToString("N"), (string?)start["name"] ?? string.Empty, new StringBuilder());
                        tools[index] = entry;
                        yield return new ToolInputStartPart(entry.Item1, entry.Item2);
                    }

                    break;

                case "contentBlockDelta":
                    var delta = evt["delta"] as JsonObject ?? [];
                    if ((string?)delta["text"] is { Length: > 0 } textDelta)
                    {
                        yield return new TextDeltaPart($"text-{index}", textDelta);
                    }
                    else if (delta["toolUse"]?["input"] is JsonValue inputValue && tools.TryGetValue(index, out var tool))
                    {
                        var partial = inputValue.GetValue<string>();
                        tool.Input.Append(partial);
                        yield return new ToolInputDeltaPart(tool.Id, partial);
                    }
                    else if (delta["reasoningContent"] is JsonObject reasoning)
                    {
                        reasoningBlocks.Add(index);
                        if ((string?)reasoning["text"] is { Length: > 0 } reasoningText)
                        {
                            yield return new ReasoningDeltaPart($"reasoning-{index}", reasoningText);
                        }

                        if ((string?)reasoning["signature"] is { } signature)
                        {
                            signatures[index] = signature;
                        }

                        if ((string?)reasoning["redactedContent"] is { } redactedContent)
                        {
                            redacted[index] = redactedContent;
                        }
                    }

                    break;

                case "contentBlockStop":
                    if (tools.Remove(index, out var finished))
                    {
                        var ok = JsonUtil.TryParseElement(finished.Input.ToString(), out var input);
                        yield return new ToolCallStreamPart(new ToolCallPart(finished.Id, finished.Name, input) { InvalidInput = !ok });
                    }
                    else if (reasoningBlocks.Remove(index))
                    {
                        yield return new ReasoningEndPart($"reasoning-{index}")
                        {
                            ProviderMetadata = ReasoningMetadata(signatures.GetValueOrDefault(index), redacted.GetValueOrDefault(index)),
                        };
                    }

                    break;

                case "messageStop":
                    finish = MapStopReason((string?)evt["stopReason"]);
                    break;

                case "metadata":
                    if (evt["usage"] is JsonObject u)
                    {
                        usage = ParseUsage(u);
                    }

                    break;
            }
        }

        yield return new ModelFinishPart(finish, usage);
    }

    // ------------------------------------------------------------------
    // Petición
    // ------------------------------------------------------------------

    internal (JsonObject Body, List<string> Warnings) BuildBody(LanguageModelRequest request)
    {
        var warnings = new List<string>();
        var body = new JsonObject { ["messages"] = ConvertMessages(request.Messages) };

        var system = new JsonArray();
        foreach (var message in request.Messages.Where(m => m.Role == ChatRole.System))
        {
            system.Add(new JsonObject { ["text"] = message.Text });
        }

        // Converse no tiene modo JSON nativo para todos los modelos: el esquema se pide por instrucción
        // y la salida se valida al parsear (Output.Object<T>).
        if (request.ResponseFormat is { Type: ResponseFormatType.Json } format)
        {
            var instruction = format.Schema is { } schema
                ? $"Responde únicamente con un objeto JSON válido (sin texto adicional ni bloques de código) que cumpla este JSON Schema:\n{schema.GetRawText()}"
                : "Responde únicamente con JSON válido, sin texto adicional.";
            system.Add(new JsonObject { ["text"] = instruction });
        }

        if (system.Count > 0)
        {
            body["system"] = system;
        }

        var inference = new JsonObject();
        if (request.MaxOutputTokens is { } max)
        {
            inference["maxTokens"] = max;
        }

        if (request.Temperature is { } temperature)
        {
            inference["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            inference["topP"] = topP;
        }

        if (request.StopSequences is { Count: > 0 } stop)
        {
            inference["stopSequences"] = new JsonArray(stop.Select(s => (JsonNode?)s).ToArray());
        }

        if (inference.Count > 0)
        {
            body["inferenceConfig"] = inference;
        }

        if (request.TopK is not null || request.Seed is not null || request.PresencePenalty is not null || request.FrequencyPenalty is not null)
        {
            warnings.Add("Converse no admite TopK, Seed ni penalizaciones de forma genérica; usa ProviderOptions[\"bedrock\"].additionalModelRequestFields.");
        }

        var functionTools = request.Tools?.Where(t => !t.IsProviderTool).ToList() ?? [];
        if (request.Tools?.Any(t => t.IsProviderTool) == true)
        {
            warnings.Add("Bedrock Converse no admite herramientas de servidor de otros proveedores; se omitieron.");
        }

        if (functionTools.Count > 0)
        {
            var toolConfig = new JsonObject
            {
                ["tools"] = new JsonArray(functionTools.Select(t =>
                {
                    var spec = new JsonObject { ["name"] = t.Name, ["inputSchema"] = new JsonObject { ["json"] = JsonUtil.ToNode(t.InputSchema) } };
                    if (t.Description is not null)
                    {
                        spec["description"] = t.Description;
                    }

                    return (JsonNode?)new JsonObject { ["toolSpec"] = spec };
                }).ToArray()),
            };

            switch (request.ToolChoice?.Kind)
            {
                case ToolChoiceKind.Required:
                    toolConfig["toolChoice"] = new JsonObject { ["any"] = new JsonObject() };
                    break;
                case ToolChoiceKind.Tool:
                    toolConfig["toolChoice"] = new JsonObject { ["tool"] = new JsonObject { ["name"] = request.ToolChoice.ToolName } };
                    break;
                case ToolChoiceKind.None:
                    // Bedrock exige toolConfig si el historial tiene llamadas a herramientas: se mantiene en modo auto.
                    warnings.Add("Bedrock Converse no admite ToolChoice.None; se usó auto.");
                    break;
            }

            body["toolConfig"] = toolConfig;
        }

        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("bedrock"));
        return (body, warnings);
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

            if (current is not null && (string?)current["role"] == role)
            {
                var content = (JsonArray)current["content"]!;
                blocks.ForEach(content.Add);
            }
            else
            {
                current = new JsonObject { ["role"] = role, ["content"] = new JsonArray(blocks.Cast<JsonNode?>().ToArray()) };
                result.Add(current);
            }
        }

        return result;
    }

    private static string? Meta(ContentPart part, string key) =>
        part.ProviderMetadata?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonObject? ConvertPart(ContentPart part)
    {
        switch (part)
        {
            case TextPart { Text.Length: > 0 } t:
                return new JsonObject { ["text"] = t.Text };

            case ImagePart { Data: not null } i:
                return new JsonObject
                {
                    ["image"] = new JsonObject { ["format"] = Format(i.MediaType), ["source"] = new JsonObject { ["bytes"] = Convert.ToBase64String(i.Data) } },
                };

            case ImagePart:
                throw new NotSupportedException("Bedrock Converse solo admite imágenes en bytes.");

            case FilePart { Data: not null } f:
                return new JsonObject
                {
                    ["document"] = new JsonObject
                    {
                        ["format"] = Format(f.MediaType),
                        ["name"] = DocumentName(f.Filename),
                        ["source"] = new JsonObject { ["bytes"] = Convert.ToBase64String(f.Data) },
                    },
                };

            case FilePart:
                throw new NotSupportedException("Bedrock Converse solo admite documentos en bytes.");

            case ReasoningPart r when Meta(r, RedactedContentKey) is { } redactedContent:
                return new JsonObject { ["reasoningContent"] = new JsonObject { ["redactedContent"] = redactedContent } };

            case ReasoningPart r when Meta(r, SignatureKey) is { } signature:
                return new JsonObject
                {
                    ["reasoningContent"] = new JsonObject { ["reasoningText"] = new JsonObject { ["text"] = r.Text, ["signature"] = signature } },
                };

            case ToolCallPart { ProviderExecuted: false } c:
                return new JsonObject { ["toolUse"] = new JsonObject { ["toolUseId"] = c.ToolCallId, ["name"] = c.ToolName, ["input"] = JsonUtil.ToNode(c.Input) } };

            case ToolResultPart { ProviderExecuted: false } r:
                JsonObject content = r.Output.ValueKind switch
                {
                    JsonValueKind.String => new JsonObject { ["text"] = r.Output.GetString() },
                    JsonValueKind.Object => new JsonObject { ["json"] = JsonUtil.ToNode(r.Output) },
                    _ => new JsonObject { ["json"] = new JsonObject { ["result"] = JsonUtil.ToNode(r.Output) } },
                };
                var toolResult = new JsonObject { ["toolUseId"] = r.ToolCallId, ["content"] = new JsonArray(content) };
                if (r.IsError)
                {
                    toolResult["status"] = "error";
                }

                return new JsonObject { ["toolResult"] = toolResult };

            default:
                return null;
        }
    }

    private static string Format(string mediaType) => mediaType switch
    {
        "image/jpeg" or "image/jpg" => "jpeg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "application/pdf" => "pdf",
        "text/plain" => "txt",
        "text/csv" => "csv",
        "text/html" => "html",
        "text/markdown" => "md",
        "application/msword" => "doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
        "application/vnd.ms-excel" => "xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
        _ => mediaType.Split('/').Last(),
    };

    /// <summary>Bedrock solo admite letras, números, espacios, guiones, paréntesis y corchetes en el nombre.</summary>
    private static string DocumentName(string? filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename ?? "documento");
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '(' or ')' or '[' or ']' ? c : ' ').ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > 0 ? clean : "documento";
    }

    // ------------------------------------------------------------------
    // Respuesta
    // ------------------------------------------------------------------

    internal static LanguageModelResult ParseResponse(JsonObject json)
    {
        var content = new List<ContentPart>();
        foreach (var block in (json["output"]?["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)block["text"] is { } text)
            {
                content.Add(new TextPart(text));
            }
            else if (block["toolUse"] is JsonObject toolUse)
            {
                content.Add(new ToolCallPart(
                    (string?)toolUse["toolUseId"] ?? Guid.NewGuid().ToString("N"),
                    (string?)toolUse["name"] ?? string.Empty,
                    toolUse["input"] is { } input ? JsonSerializer.SerializeToElement(input) : JsonUtil.EmptyObject));
            }
            else if (block["reasoningContent"] is JsonObject reasoning)
            {
                var reasoningText = reasoning["reasoningText"] as JsonObject;
                content.Add(new ReasoningPart((string?)reasoningText?["text"] ?? string.Empty)
                {
                    ProviderMetadata = ReasoningMetadata((string?)reasoningText?["signature"], (string?)reasoning["redactedContent"]),
                });
            }
        }

        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MapStopReason((string?)json["stopReason"]),
            Usage = json["usage"] is JsonObject u ? ParseUsage(u) : Usage.Empty,
            Response = new ResponseMetadata(null, null, DateTimeOffset.UtcNow),
        };
    }

    private static Dictionary<string, JsonElement>? ReasoningMetadata(string? signature, string? redactedContent)
    {
        var metadata = new Dictionary<string, JsonElement>();
        if (signature is not null)
        {
            metadata[SignatureKey] = JsonSerializer.SerializeToElement(signature);
        }

        if (redactedContent is not null)
        {
            metadata[RedactedContentKey] = JsonSerializer.SerializeToElement(redactedContent);
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static Usage ParseUsage(JsonObject u) => new()
    {
        InputTokens = (int?)u["inputTokens"],
        OutputTokens = (int?)u["outputTokens"],
        CachedInputTokens = (int?)u["cacheReadInputTokens"],
    };

    private static FinishReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => FinishReason.Stop,
        "tool_use" => FinishReason.ToolCalls,
        "max_tokens" or "model_context_window_exceeded" => FinishReason.Length,
        "guardrail_intervened" or "content_filtered" => FinishReason.ContentFilter,
        null => FinishReason.Unknown,
        _ => FinishReason.Other,
    };
}

/// <summary>Embeddings en Bedrock (<c>/invoke</c>): Amazon Titan Text Embeddings y Cohere Embed.</summary>
public sealed class BedrockEmbeddingModel : IEmbeddingModel
{
    private readonly BedrockProvider _provider;
    private readonly int? _dimensions;

    internal BedrockEmbeddingModel(string modelId, int? dimensions, BedrockProvider provider)
    {
        ModelId = modelId;
        _dimensions = dimensions;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    private bool IsCohere => ModelId.Contains("cohere.embed", StringComparison.Ordinal);

    /// <summary>Titan: un texto por llamada; Cohere: hasta 96.</summary>
    public int? MaxEmbeddingsPerCall => IsCohere ? 96 : 1;

    public async Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        if (IsCohere)
        {
            var json = await InvokeAsync(new JsonObject
            {
                ["texts"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()),
                ["input_type"] = "search_document",
            }, cancellationToken).ConfigureAwait(false);
            var vectors = (json["embeddings"] as JsonArray ?? []).Select(e => (e as JsonArray ?? []).Select(x => (float)x!).ToArray()).ToList();
            return new EmbeddingModelResult(vectors, null);
        }

        var embeddings = new List<float[]>();
        var tokens = 0;
        foreach (var value in values)
        {
            var body = new JsonObject { ["inputText"] = value, ["normalize"] = true };
            if (_dimensions is { } d)
            {
                body["dimensions"] = d;
            }

            var json = await InvokeAsync(body, cancellationToken).ConfigureAwait(false);
            embeddings.Add((json["embedding"] as JsonArray ?? []).Select(x => (float)x!).ToArray());
            tokens += (int?)json["inputTextTokenCount"] ?? 0;
        }

        return new EmbeddingModelResult(embeddings, new EmbeddingUsage(tokens));
    }

    private async Task<JsonObject> InvokeAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var http = await _provider.CreateRequestAsync(_provider.ModelUrl(ModelId, "invoke"), body, stream: false, cancellationToken).ConfigureAwait(false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        return await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
