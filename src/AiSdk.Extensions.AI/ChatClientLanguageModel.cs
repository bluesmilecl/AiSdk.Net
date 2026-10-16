using System.Runtime.CompilerServices;
using System.Text.Json;
using AiSdk.Internal;
using Microsoft.Extensions.AI;
using MEAI = Microsoft.Extensions.AI;

namespace AiSdk.Extensions.AI;

/// <summary>
/// Adapta cualquier <see cref="IChatClient"/> de Microsoft.Extensions.AI (Azure OpenAI,
/// Ollama, Gemini, Bedrock...) a <see cref="ILanguageModel"/>, para usarlo con
/// <see cref="Ai"/>, agentes y middleware de este SDK.
/// </summary>
public sealed class ChatClientLanguageModel(IChatClient client, string provider, string modelId) : ILanguageModel
{
    public string Provider { get; } = provider;

    public string ModelId { get; } = modelId;

    public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        var response = await client.GetResponseAsync(ToChatMessages(request.Messages), ToChatOptions(request), cancellationToken).ConfigureAwait(false);
        var content = response.Messages.SelectMany(m => m.Contents).Select(FromAIContent).OfType<ContentPart>().ToList();
        return new LanguageModelResult
        {
            Content = content,
            FinishReason = MapFinish(response.FinishReason, content),
            Usage = FromUsage(response.Usage),
            Response = new ResponseMetadata(response.ResponseId, response.ModelId, response.CreatedAt ?? DateTimeOffset.UtcNow),
        };
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatFinishReason? finish = null;
        var usage = Usage.Empty;
        var hasToolCalls = false;
        var sentMetadata = false;
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
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        yield return new TextDeltaPart("text-0", t.Text);
                        break;
                    case TextReasoningContent r when !string.IsNullOrEmpty(r.Text):
                        yield return new ReasoningDeltaPart("reasoning-0", r.Text);
                        break;
                    case FunctionCallContent f:
                        hasToolCalls = true;
                        var call = (ToolCallPart)FromAIContent(f)!;
                        yield return new ToolInputStartPart(call.ToolCallId, call.ToolName);
                        yield return new ToolCallStreamPart(call);
                        break;
                    case UsageContent u:
                        usage = FromUsage(u.Details);
                        break;
                }
            }
        }

        yield return new ModelFinishPart(finish is null && hasToolCalls ? FinishReason.ToolCalls : MapFinish(finish, []), usage);
    }

    private static List<ChatMessage> ToChatMessages(IReadOnlyList<ModelMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var m in messages)
        {
            var role = m.Role switch
            {
                ChatRole.System => MEAI.ChatRole.System,
                ChatRole.User => MEAI.ChatRole.User,
                ChatRole.Assistant => MEAI.ChatRole.Assistant,
                _ => MEAI.ChatRole.Tool,
            };
            result.Add(new ChatMessage(role, m.Content.Select(ToAIContent).OfType<AIContent>().ToList()));
        }

        return result;
    }

    private static AIContent? ToAIContent(ContentPart part) => part switch
    {
        TextPart t => new TextContent(t.Text),
        ReasoningPart r => new TextReasoningContent(r.Text),
        ImagePart { Data: not null } i => new DataContent(i.Data, i.MediaType),
        ImagePart { Url: not null } i => new UriContent(i.Url, i.MediaType),
        FilePart { Data: not null } f => new DataContent(f.Data, f.MediaType) { Name = f.Filename },
        FilePart { Url: not null } f => new UriContent(f.Url, f.MediaType),
        ToolCallPart c => new FunctionCallContent(c.ToolCallId, c.ToolName, c.Input.Deserialize<Dictionary<string, object?>>()),
        ToolResultPart r => new FunctionResultContent(r.ToolCallId, r.Output),
        _ => null,
    };

    private static ContentPart? FromAIContent(AIContent content) => content switch
    {
        TextContent t when !string.IsNullOrEmpty(t.Text) => new TextPart(t.Text),
        TextReasoningContent r => new ReasoningPart(r.Text),
        FunctionCallContent f => new ToolCallPart(
            f.CallId,
            f.Name,
            f.Arguments is null ? JsonUtil.EmptyObject : JsonSerializer.SerializeToElement(f.Arguments, AIJsonUtilities.DefaultOptions)),
        _ => null,
    };

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

        if (request.Tools is { Count: > 0 } tools)
        {
            options.Tools = tools
                .Select(t => (AITool)AIFunctionFactory.CreateDeclaration(t.Name, t.Description, t.InputSchema))
                .ToList();
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

        return options;
    }

    private static Usage FromUsage(UsageDetails? usage) => usage is null
        ? Usage.Empty
        : new Usage
        {
            InputTokens = (int?)usage.InputTokenCount,
            OutputTokens = (int?)usage.OutputTokenCount,
        };

    private static FinishReason MapFinish(ChatFinishReason? reason, IReadOnlyList<ContentPart> content)
    {
        if (reason == ChatFinishReason.Stop)
        {
            return content.OfType<ToolCallPart>().Any() ? FinishReason.ToolCalls : FinishReason.Stop;
        }

        if (reason == ChatFinishReason.Length)
        {
            return FinishReason.Length;
        }

        if (reason == ChatFinishReason.ToolCalls)
        {
            return FinishReason.ToolCalls;
        }

        if (reason == ChatFinishReason.ContentFilter)
        {
            return FinishReason.ContentFilter;
        }

        return content.OfType<ToolCallPart>().Any() ? FinishReason.ToolCalls : reason is null ? FinishReason.Unknown : FinishReason.Other;
    }
}

/// <summary>Adapta un <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> a <see cref="IEmbeddingModel"/>.</summary>
public sealed class EmbeddingGeneratorModel(IEmbeddingGenerator<string, Embedding<float>> generator, string provider, string modelId) : IEmbeddingModel
{
    public string Provider { get; } = provider;

    public string ModelId { get; } = modelId;

    public int? MaxEmbeddingsPerCall => null;

    public async Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var result = await generator.GenerateAsync(values, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new EmbeddingModelResult(
            result.Select(e => e.Vector.ToArray()).ToList(),
            result.Usage?.InputTokenCount is { } tokens ? new EmbeddingUsage((int)tokens) : null);
    }
}

public static class MicrosoftExtensionsAIExtensions
{
    /// <summary>Convierte un <see cref="IChatClient"/> en <see cref="ILanguageModel"/>.</summary>
    public static ILanguageModel AsLanguageModel(this IChatClient client, string? provider = null, string? modelId = null)
    {
        var metadata = client.GetService<ChatClientMetadata>();
        return new ChatClientLanguageModel(
            client,
            provider ?? metadata?.ProviderName ?? "meai",
            modelId ?? metadata?.DefaultModelId ?? "default");
    }

    /// <summary>Convierte un <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> en <see cref="IEmbeddingModel"/>.</summary>
    public static IEmbeddingModel AsEmbeddingModel(this IEmbeddingGenerator<string, Embedding<float>> generator, string? provider = null, string? modelId = null)
    {
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        return new EmbeddingGeneratorModel(
            generator,
            provider ?? metadata?.ProviderName ?? "meai",
            modelId ?? metadata?.DefaultModelId ?? "default");
    }
}
