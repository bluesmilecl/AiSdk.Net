using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using MEAI = Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Exposes an AiSdk <see cref="ILanguageModel"/> as a Microsoft.Extensions.AI <see cref="IChatClient"/>, so any code
/// built on Microsoft.Extensions.AI (Semantic Kernel, Microsoft Agent Framework, <c>FunctionInvokingChatClient</c>,
/// evaluation libraries...) can use an AiSdk provider. Usually created with
/// <see cref="MicrosoftExtensionsAIExtensions.AsIChatClient(ILanguageModel, Uri?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each request makes exactly one model call and never executes tools: function calls are returned as
/// <see cref="FunctionCallContent"/> so a <c>FunctionInvokingChatClient</c> (or the caller) runs them. Only the
/// declarations of <see cref="ChatOptions.Tools"/> (name, description and JSON schema) reach the model; tools that are
/// not <see cref="AIFunctionDeclaration"/> (hosted tools) are ignored.
/// </para>
/// <para>
/// <see cref="ChatOptions.RawRepresentationFactory"/> may return a <see cref="LanguageModelRequest"/> (whose settings,
/// tools, provider options and headers take precedence over <see cref="ChatOptions"/>; its messages are replaced by the
/// conversation) or a <see cref="AiSdk.ProviderOptions"/> instance, to pass provider-specific options such as
/// reasoning effort. <see cref="ChatOptions.ModelId"/>, <see cref="ChatOptions.ConversationId"/>,
/// <see cref="ChatOptions.AllowMultipleToolCalls"/> and <see cref="ChatOptions.Reasoning"/> are not mapped: the
/// model is fixed and AiSdk models are stateless.
/// </para>
/// </remarks>
public sealed class LanguageModelChatClient : IChatClient
{
    private readonly ILanguageModel _model;
    private readonly ChatClientMetadata _metadata;

    /// <summary>Creates the adapter.</summary>
    /// <param name="model">The AiSdk model to expose.</param>
    /// <param name="providerUri">Endpoint reported in <see cref="ChatClientMetadata.ProviderUri"/>, if known.</param>
    public LanguageModelChatClient(ILanguageModel model, Uri? providerUri = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _metadata = new ChatClientMetadata(model.Provider, providerUri, model.ModelId);
    }

    internal ILanguageModel Model => _model;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _model.GenerateAsync(CreateRequest(messages, options), cancellationToken).ConfigureAwait(false);
        var message = new ChatMessage(MEAI.ChatRole.Assistant, [.. result.Content.Select(MeaiMapper.ToAIContent)])
        {
            MessageId = result.Response?.Id,
            CreatedAt = result.Response?.Timestamp,
        };

        return new ChatResponse(message)
        {
            ResponseId = result.Response?.Id,
            ModelId = result.Response?.ModelId ?? _model.ModelId,
            CreatedAt = result.Response?.Timestamp,
            FinishReason = MeaiMapper.ToChatFinishReason(result.FinishReason),
            Usage = MeaiMapper.ToUsageDetails(result.Usage),
            RawRepresentation = result,
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var request = CreateRequest(messages, options);
        ResponseMetadata? response = null;
        await foreach (var part in _model.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AIContent? content = null;
            switch (part)
            {
                case ResponseMetadataPart m:
                    response = m.Metadata;
                    continue;
                case ErrorPart e:
                    ExceptionDispatchInfo.Throw(e.Error);
                    break;
                case ModelFinishPart f:
                    var update = CreateUpdate(part, response, MeaiMapper.ToUsageDetails(f.Usage) is { } usage ? [new UsageContent(usage)] : []);
                    update.FinishReason = MeaiMapper.ToChatFinishReason(f.FinishReason);
                    yield return update;
                    continue;
                case TextDeltaPart t:
                    content = new TextContent(t.Delta);
                    break;
                case ReasoningDeltaPart r:
                    content = new TextReasoningContent(r.Delta);
                    break;
                case ReasoningEndPart { ProviderMetadata.Count: > 0 } r:
                    // An empty block that only carries the signature: Microsoft.Extensions.AI merges it into the preceding deltas.
                    content = new TextReasoningContent(string.Empty) { ProtectedData = MeaiMapper.EncodeProtectedData(r.ProviderMetadata) };
                    break;
                case ToolCallStreamPart c:
                    content = MeaiMapper.ToAIContent(c.ToolCall);
                    break;
                case ToolResultStreamPart r:
                    content = MeaiMapper.ToAIContent(r.ToolResult);
                    break;
                case SourceStreamPart s:
                    content = MeaiMapper.ToAIContent(s.Source);
                    break;
                case ContentStreamPart c:
                    content = MeaiMapper.ToAIContent(c.Content);
                    break;
            }

            if (content is not null)
            {
                yield return CreateUpdate(part, response, [content]);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <see cref="ChatClientMetadata"/> (AiSdk provider name, model id and endpoint), the wrapped
    /// <see cref="ILanguageModel"/> (or any type it implements) and the client itself. Keyed services are not supported.
    /// </remarks>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : serviceType.IsInstanceOfType(_model) ? _model
            : null;
    }

    /// <summary>Does nothing: the adapter does not own the wrapped model.</summary>
    public void Dispose()
    {
    }

    private ChatResponseUpdate CreateUpdate(StreamPart part, ResponseMetadata? response, IList<AIContent> contents) =>
        new(MEAI.ChatRole.Assistant, contents)
        {
            ResponseId = response?.Id,
            MessageId = response?.Id,
            ModelId = response?.ModelId ?? _model.ModelId,
            CreatedAt = response?.Timestamp,
            RawRepresentation = part,
        };

    private LanguageModelRequest CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var raw = options?.RawRepresentationFactory?.Invoke(this);
        var baseRequest = raw as LanguageModelRequest;
        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var baseTools = baseRequest?.Tools ?? [];
        IReadOnlyList<ToolDefinition> tools =
        [
            .. baseTools,
            .. options?.Tools?.OfType<AIFunctionDeclaration>()
                .Where(f => !baseTools.Any(t => t.Name == f.Name))
                .Select(f => ToToolDefinition(f, options)) ?? [],
        ];

        return (baseRequest ?? new LanguageModelRequest { Messages = [] }) with
        {
            Messages = ToModelMessages(messageList, options?.Instructions),
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = tools.Count == 0 ? null : baseRequest?.ToolChoice ?? ToToolChoice(options?.ToolMode),
            ResponseFormat = baseRequest?.ResponseFormat ?? ToResponseFormat(options?.ResponseFormat),
            MaxOutputTokens = baseRequest?.MaxOutputTokens ?? options?.MaxOutputTokens,
            Temperature = baseRequest?.Temperature ?? MeaiMapper.ToDouble(options?.Temperature),
            TopP = baseRequest?.TopP ?? MeaiMapper.ToDouble(options?.TopP),
            TopK = baseRequest?.TopK ?? options?.TopK,
            PresencePenalty = baseRequest?.PresencePenalty ?? MeaiMapper.ToDouble(options?.PresencePenalty),
            FrequencyPenalty = baseRequest?.FrequencyPenalty ?? MeaiMapper.ToDouble(options?.FrequencyPenalty),
            StopSequences = baseRequest?.StopSequences ?? (options?.StopSequences is { Count: > 0 } stop ? [.. stop] : null),
            Seed = baseRequest?.Seed ?? (options?.Seed is { } seed ? unchecked((int)seed) : null),
            ProviderOptions = baseRequest?.ProviderOptions ?? raw as ProviderOptions,
        };
    }

    private static List<ModelMessage> ToModelMessages(IReadOnlyList<ChatMessage> messages, string? instructions)
    {
        var toolNames = MeaiMapper.CollectToolNames(messages.SelectMany(m => m.Contents));
        var result = new List<ModelMessage>(messages.Count + 1);
        if (!string.IsNullOrEmpty(instructions))
        {
            result.Add(ModelMessage.System(instructions));
        }

        foreach (var message in messages)
        {
            var role = MeaiMapper.FromChatRole(message.Role);
            var isAssistant = role == ChatRole.Assistant;
            List<ContentPart> parts = [];
            List<ContentPart> toolResults = [];
            foreach (var content in message.Contents)
            {
                switch (MeaiMapper.ToContentPart(content, isAssistant, toolNames))
                {
                    case ToolResultPart { ProviderExecuted: false } r:
                        toolResults.Add(r);
                        break;
                    case { } part:
                        parts.Add(part);
                        break;
                }
            }

            // Tool results always travel in a tool message, ahead of anything else the message carries.
            if (toolResults.Count > 0)
            {
                result.Add(new ModelMessage(ChatRole.Tool, toolResults));
            }

            if (parts.Count > 0)
            {
                result.Add(new ModelMessage(role == ChatRole.Tool ? ChatRole.User : role, parts));
            }
        }

        return result;
    }

    private static ToolDefinition ToToolDefinition(AIFunctionDeclaration function, ChatOptions options) =>
        new(function.Name, string.IsNullOrEmpty(function.Description) ? null : function.Description, MeaiMapper.SchemaOrEmpty(function.JsonSchema))
        {
            // Same convention as the OpenAI client of Microsoft.Extensions.AI: per tool, or for every tool in the options.
            Strict = IsStrict(function.AdditionalProperties) ?? IsStrict(options.AdditionalProperties) ?? false,
        };

    private static bool? IsStrict(IReadOnlyDictionary<string, object?>? properties) =>
        properties?.TryGetValue("strictJsonSchema", out var value) == true && value is bool strict ? strict : null;

    private static ToolChoice? ToToolChoice(ChatToolMode? mode) => mode switch
    {
        null => null,
        NoneChatToolMode => ToolChoice.None,
        RequiredChatToolMode { RequiredFunctionName: { } name } => ToolChoice.Tool(name),
        RequiredChatToolMode => ToolChoice.Required,
        _ => ToolChoice.Auto,
    };

    private static ResponseFormat? ToResponseFormat(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson json => ResponseFormat.Json(json.Schema, json.SchemaName, json.SchemaDescription),
        _ => null,
    };
}
