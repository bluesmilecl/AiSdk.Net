using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MEAI = Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Adapts any Microsoft.Extensions.AI <see cref="IChatClient"/> (Azure OpenAI, Ollama, Gemini, Bedrock...) to
/// <see cref="ILanguageModel"/>, so it can be used with <see cref="Ai"/>, agents and middleware of this SDK. Usually
/// created with <see cref="MicrosoftExtensionsAIExtensions.AsLanguageModel(IChatClient, string?, string?)"/>.
/// </summary>
/// <remarks>
/// Tools are sent as declarations only: AiSdk runs them in its own loop. Provider-defined tools, provider options and
/// headers have no Microsoft.Extensions.AI equivalent; they only reach the model when the client pipeline ends in a
/// <see cref="LanguageModelChatClient"/> (an AiSdk model exposed with
/// <see cref="MicrosoftExtensionsAIExtensions.AsChatClient(ILanguageModel, Uri?)"/>).
/// </remarks>
/// <param name="client">The Microsoft.Extensions.AI chat client to adapt.</param>
/// <param name="provider">Provider name reported by <see cref="Provider"/>.</param>
/// <param name="modelId">Model id reported by <see cref="ModelId"/>.</param>
public sealed class ChatClientLanguageModel(IChatClient client, string provider, string modelId) : ILanguageModel
{
    /// <inheritdoc />
    public string Provider { get; } = provider;

    /// <inheritdoc />
    public string ModelId { get; } = modelId;

    internal IChatClient Client => client;

    /// <inheritdoc />
    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var response = await client.GetResponseAsync(ToChatMessages(request.Messages), ToChatOptions(request), cancellationToken).ConfigureAwait(false);
        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        var toolNames = MeaiMapper.CollectToolNames(contents);

        // A call whose result is already in the response was executed by the provider or the client pipeline: AiSdk must not run it again.
        var executed = contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        var content = contents
            .Select(c => MeaiMapper.ToContentPart(c, inAssistantMessage: true, toolNames))
            .OfType<ContentPart>()
            .Select(p => p is ToolCallPart { ProviderExecuted: false } call && executed.Contains(call.ToolCallId) ? call with { ProviderExecuted = true } : p)
            .ToList();

        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MeaiMapper.FromChatFinishReason(response.FinishReason, content.OfType<ToolCallPart>().Any(c => !c.ProviderExecuted)),
            Usage = MeaiMapper.FromUsageDetails(response.Usage),
            Response = new ResponseMetadata(response.ResponseId, response.ModelId, response.CreatedAt ?? DateTimeOffset.UtcNow),
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatFinishReason? finish = null;
        var usage = Usage.Empty;
        var hasToolCalls = false;
        var sentMetadata = false;
        var reasoningIndex = 0;
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var providerExecuted = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var update in client.GetStreamingResponseAsync(ToChatMessages(request.Messages), ToChatOptions(request), cancellationToken).ConfigureAwait(false))
        {
            if (!sentMetadata && update.ResponseId is not null)
            {
                sentMetadata = true;
                yield return new ResponseMetadataPart(new ResponseMetadata(update.ResponseId, update.ModelId, update.CreatedAt ?? DateTimeOffset.UtcNow));
            }

            finish = update.FinishReason ?? finish;
            foreach (var c in update.Contents)
            {
                switch (c)
                {
                    case TextContent t:
                        if (!string.IsNullOrEmpty(t.Text))
                        {
                            yield return new TextDeltaPart("text-0", t.Text);
                        }

                        break;
                    case TextReasoningContent r:
                        var reasoningId = $"reasoning-{reasoningIndex}";
                        if (!string.IsNullOrEmpty(r.Text))
                        {
                            yield return new ReasoningDeltaPart(reasoningId, r.Text);
                        }

                        // The protected data (signature, encrypted reasoning...) closes the block and is replayed on the next turn.
                        if (r.ProtectedData is not null)
                        {
                            yield return new ReasoningEndPart(reasoningId) { ProviderMetadata = MeaiMapper.DecodeProtectedData(r.ProtectedData) };
                            reasoningIndex++;
                        }

                        break;
                    case FunctionCallContent f:
                        toolNames[f.CallId] = f.Name;
                        var call = (ToolCallPart)MeaiMapper.ToContentPart(f, inAssistantMessage: true, toolNames)!;
                        if (call.ProviderExecuted)
                        {
                            providerExecuted.Add(call.ToolCallId);
                        }
                        else
                        {
                            hasToolCalls = true;
                        }

                        yield return new ToolInputStartPart(call.ToolCallId, call.ToolName);
                        yield return new ToolCallStreamPart(call);
                        break;
                    case FunctionResultContent result:
                        // Only results of provider-executed calls belong to the model output.
                        if (providerExecuted.Contains(result.CallId))
                        {
                            yield return new ToolResultStreamPart((ToolResultPart)MeaiMapper.ToContentPart(result, inAssistantMessage: true, toolNames)!);
                        }

                        break;
                    case UsageContent u:
                        usage = MeaiMapper.FromUsageDetails(u.Details);
                        break;
                    default:
                        switch (MeaiMapper.ToContentPart(c, inAssistantMessage: true, toolNames))
                        {
                            case SourcePart source:
                                yield return new SourceStreamPart(source);
                                break;
                            case { } part:
                                yield return new ContentStreamPart(part);
                                break;
                        }

                        break;
                }
            }
        }

        yield return new ModelFinishPart(MeaiMapper.FromChatFinishReason(finish, hasToolCalls), usage);
    }

    private static List<ChatMessage> ToChatMessages(IReadOnlyList<ModelMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            var role = m.Role switch
            {
                ChatRole.System => MEAI.ChatRole.System,
                ChatRole.User => MEAI.ChatRole.User,
                ChatRole.Assistant => MEAI.ChatRole.Assistant,
                _ => MEAI.ChatRole.Tool,
            };

            // Provider-executed tools, citations and raw provider blocks mean nothing to another client.
            var parts = m.Content.Where(p => p is not ToolCallPart { ProviderExecuted: true }
                and not ToolResultPart { ProviderExecuted: true }
                and not SourcePart
                and not RawProviderPart);
            result.Add(new ChatMessage(role, [.. parts.Select(MeaiMapper.ToAIContent)]));
        }

        return result;
    }

    private static ChatOptions ToChatOptions(LanguageModelRequest request)
    {
        var options = new ChatOptions
        {
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = (float?)request.Temperature,
            TopP = (float?)request.TopP,
            TopK = request.TopK,
            PresencePenalty = (float?)request.PresencePenalty,
            FrequencyPenalty = (float?)request.FrequencyPenalty,
            Seed = request.Seed,
            StopSequences = request.StopSequences?.ToList(),
        };

        if (request.Tools?.Where(t => !t.IsProviderTool).ToList() is { Count: > 0 } tools)
        {
            options.Tools = [.. tools.Select(t => (AITool)AIFunctionFactory.CreateDeclaration(t.Name, t.Description, t.InputSchema))];
            options.ToolMode = request.ToolChoice?.Kind switch
            {
                ToolChoiceKind.None => ChatToolMode.None,
                ToolChoiceKind.Required => ChatToolMode.RequireAny,
                ToolChoiceKind.Tool => ChatToolMode.RequireSpecific(request.ToolChoice.ToolName!),
                _ => ChatToolMode.Auto,
            };
        }

        if (request.ResponseFormat is { Type: ResponseFormatType.Json } format)
        {
            options.ResponseFormat = format.Schema is { } schema
                ? ChatResponseFormat.ForJsonSchema(schema, format.Name, format.Description)
                : ChatResponseFormat.Json;
        }

        // What ChatOptions cannot express (provider options, headers, provider-defined tools) reaches an AiSdk model at
        // the end of the pipeline as its raw representation; any other client ignores it.
        var providerTools = request.Tools?.Where(t => t.IsProviderTool).ToList();
        if (request.ProviderOptions is not null || request.Headers is not null || providerTools is { Count: > 0 })
        {
            options.RawRepresentationFactory = c => c is LanguageModelChatClient
                ? new LanguageModelRequest
                {
                    Messages = [],
                    ProviderOptions = request.ProviderOptions,
                    Headers = request.Headers,
                    Tools = providerTools is { Count: > 0 } ? providerTools : null,
                }
                : null;
        }

        return options;
    }
}
