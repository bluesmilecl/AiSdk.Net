using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.OpenAI;

/// <summary>Modelo de lenguaje sobre la API Chat Completions de OpenAI.</summary>
public sealed class OpenAIChatLanguageModel : ILanguageModel
{
    private readonly OpenAIProvider _provider;

    internal OpenAIChatLanguageModel(string modelId, OpenAIProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request, stream: false);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("chat/completions"), body, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(json);
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request, stream: true);
        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("chat/completions"), body, _provider.Headers(request.Headers), stream: true);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: true, cancellationToken).ConfigureAwait(false);

        var toolCalls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        var finish = FinishReason.Unknown;
        var usage = Usage.Empty;
        var sentMetadata = false;

        await foreach (var sse in ProviderHttp.ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (sse.Data == "[DONE]")
            {
                break;
            }

            var chunk = JsonNode.Parse(sse.Data) as JsonObject;
            if (chunk is null)
            {
                continue;
            }

            if (chunk["error"] is JsonObject error)
            {
                yield return new ErrorPart(new AiSdkException((string?)error["message"] ?? "Error en el stream de OpenAI."));
                yield break;
            }

            if (!sentMetadata && chunk["id"] is not null)
            {
                sentMetadata = true;
                yield return new ResponseMetadataPart(Metadata(chunk));
            }

            if (chunk["usage"] is JsonObject u)
            {
                usage = ParseUsage(u);
            }

            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
            {
                continue;
            }

            if ((string?)choice["finish_reason"] is { } fr)
            {
                finish = MapFinishReason(fr);
            }

            if (choice["delta"] is not JsonObject delta)
            {
                continue;
            }

            if (((string?)delta["reasoning_content"] ?? (string?)delta["reasoning"]) is { Length: > 0 } reasoning)
            {
                yield return new ReasoningDeltaPart("reasoning-0", reasoning);
            }

            if ((string?)delta["content"] is { Length: > 0 } content)
            {
                yield return new TextDeltaPart("text-0", content);
            }

            if (delta["tool_calls"] is JsonArray calls)
            {
                foreach (var callNode in calls.OfType<JsonObject>())
                {
                    var index = (int?)callNode["index"] ?? 0;
                    var fn = callNode["function"] as JsonObject;
                    if (!toolCalls.TryGetValue(index, out var entry))
                    {
                        entry = ((string?)callNode["id"] ?? $"call_{index}", (string?)fn?["name"] ?? string.Empty, new StringBuilder());
                        toolCalls[index] = entry;
                        yield return new ToolInputStartPart(entry.Id, entry.Name);
                    }

                    if ((string?)fn?["arguments"] is { Length: > 0 } args)
                    {
                        entry.Args.Append(args);
                        yield return new ToolInputDeltaPart(entry.Id, args);
                    }
                }
            }
        }

        foreach (var (_, call) in toolCalls)
        {
            yield return new ToolCallStreamPart(ToToolCall(call.Id, call.Name, call.Args.ToString()));
        }

        yield return new ModelFinishPart(finish, usage);
    }

    internal JsonObject BuildBody(LanguageModelRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["messages"] = ConvertMessages(request.Messages),
        };

        if (request.MaxOutputTokens is { } max)
        {
            body[_provider.Options.UseLegacyMaxTokens ? "max_tokens" : "max_completion_tokens"] = max;
        }

        Set(body, "temperature", request.Temperature);
        Set(body, "top_p", request.TopP);
        Set(body, "presence_penalty", request.PresencePenalty);
        Set(body, "frequency_penalty", request.FrequencyPenalty);
        Set(body, "seed", request.Seed);
        if (request.StopSequences is { Count: > 0 } stop)
        {
            body["stop"] = new JsonArray(stop.Select(s => (JsonNode?)s).ToArray());
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonUtil.ToNode(t.Strict ? JsonSchema.ToStrict(t.InputSchema) : t.InputSchema),
                    ["strict"] = t.Strict ? true : null,
                },
            }).ToArray());

            if (request.ToolChoice is { } choice)
            {
                body["tool_choice"] = choice.Kind switch
                {
                    ToolChoiceKind.Auto => "auto",
                    ToolChoiceKind.None => "none",
                    ToolChoiceKind.Required => "required",
                    _ => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = choice.ToolName } },
                };
            }
        }

        if (request.ResponseFormat is { Type: ResponseFormatType.Json } format)
        {
            body["response_format"] = format.Schema is { } schema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = format.Name ?? "response",
                        ["description"] = format.Description,
                        ["schema"] = JsonUtil.ToNode(JsonSchema.ToStrict(schema)),
                        ["strict"] = true,
                    },
                }
                : new JsonObject { ["type"] = "json_object" };
        }

        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        RemoveNulls(body);
        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("openai"));
        if (!string.Equals(Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            JsonUtil.DeepMerge(body, request.ProviderOptions?.For(Provider));
        }

        return body;
    }

    private static JsonArray ConvertMessages(IReadOnlyList<ModelMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    result.Add(new JsonObject { ["role"] = "system", ["content"] = message.Text });
                    break;

                case ChatRole.User:
                    if (message.Content is [TextPart only])
                    {
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = only.Text });
                        break;
                    }

                    var parts = new JsonArray();
                    foreach (var part in message.Content)
                    {
                        JsonNode? converted = part switch
                        {
                            TextPart t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
                            ImagePart i => new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = i.Url?.ToString() ?? ProviderHttp.ToDataUrl(i.Data!, i.MediaType) },
                            },
                            FilePart { MediaType: var mt } f when mt.StartsWith("audio/", StringComparison.Ordinal) && f.Data is not null => new JsonObject
                            {
                                ["type"] = "input_audio",
                                ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(f.Data), ["format"] = mt.Contains("mp3") || mt.Contains("mpeg") ? "mp3" : "wav" },
                            },
                            FilePart f => new JsonObject
                            {
                                ["type"] = "file",
                                ["file"] = new JsonObject
                                {
                                    ["filename"] = f.Filename ?? "file",
                                    ["file_data"] = f.Data is not null ? ProviderHttp.ToDataUrl(f.Data, f.MediaType) : f.Url?.ToString(),
                                },
                            },
                            _ => null,
                        };
                        if (converted is not null)
                        {
                            parts.Add(converted);
                        }
                    }

                    result.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    break;

                case ChatRole.Assistant:
                    var text = message.Text;
                    var calls = message.Content.OfType<ToolCallPart>().ToList();
                    var assistant = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = text.Length > 0 || calls.Count == 0 ? text : null,
                    };
                    if (calls.Count > 0)
                    {
                        assistant["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode?)new JsonObject
                        {
                            ["id"] = c.ToolCallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.ToolName, ["arguments"] = c.Input.GetRawText() },
                        }).ToArray());
                    }

                    result.Add(assistant);
                    break;

                case ChatRole.Tool:
                    foreach (var r in message.Content.OfType<ToolResultPart>())
                    {
                        result.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = r.ToolCallId,
                            ["content"] = r.Output.ValueKind == JsonValueKind.String ? r.Output.GetString() : r.Output.GetRawText(),
                        });
                    }

                    break;
            }
        }

        return result;
    }

    internal static LanguageModelResult ParseResponse(JsonObject json)
    {
        var choice = json["choices"]?[0] as JsonObject ?? throw new AiSdkException("La respuesta de OpenAI no contiene 'choices'.");
        var message = choice["message"] as JsonObject;
        var content = new List<ContentPart>();

        if (((string?)message?["reasoning_content"] ?? (string?)message?["reasoning"]) is { Length: > 0 } reasoning)
        {
            content.Add(new ReasoningPart(reasoning));
        }

        if ((string?)message?["content"] is { Length: > 0 } text)
        {
            content.Add(new TextPart(text));
        }

        if (message?["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls.OfType<JsonObject>())
            {
                content.Add(ToToolCall(
                    (string?)call["id"] ?? Guid.NewGuid().ToString("N"),
                    (string?)call["function"]?["name"] ?? string.Empty,
                    (string?)call["function"]?["arguments"]));
            }
        }

        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MapFinishReason((string?)choice["finish_reason"]),
            Usage = json["usage"] is JsonObject u ? ParseUsage(u) : Usage.Empty,
            Response = Metadata(json),
        };
    }

    private static ToolCallPart ToToolCall(string id, string name, string? arguments)
    {
        var ok = JsonUtil.TryParseElement(arguments, out var input);
        return new ToolCallPart(id, name, input) { InvalidInput = !ok };
    }

    private static ResponseMetadata Metadata(JsonObject json) => new(
        (string?)json["id"],
        (string?)json["model"],
        json["created"] is JsonValue created && created.TryGetValue<long>(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow);

    private static Usage ParseUsage(JsonObject u) => new()
    {
        InputTokens = (int?)u["prompt_tokens"],
        OutputTokens = (int?)u["completion_tokens"],
        ReasoningTokens = (int?)u["completion_tokens_details"]?["reasoning_tokens"],
        CachedInputTokens = (int?)u["prompt_tokens_details"]?["cached_tokens"],
    };

    internal static FinishReason MapFinishReason(string? reason) => reason switch
    {
        "stop" => FinishReason.Stop,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "tool_calls" or "function_call" => FinishReason.ToolCalls,
        null => FinishReason.Unknown,
        _ => FinishReason.Other,
    };

    private static void Set<T>(JsonObject body, string key, T? value) where T : struct
    {
        if (value is { } v)
        {
            body[key] = JsonValue.Create(v);
        }
    }

    private static void RemoveNulls(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                // Se conserva "content": null en mensajes de assistant con tool_calls.
                if (key != "content")
                {
                    obj.Remove(key);
                }
            }

            foreach (var (_, child) in obj)
            {
                RemoveNulls(child);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                RemoveNulls(item);
            }
        }
    }
}
