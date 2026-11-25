using System.Runtime.CompilerServices;
using System.Text.Json;
using AiSdk.Internal;

namespace AiSdk.Testing;

/// <summary>
/// Mock model for unit tests (equivalent to <c>MockLanguageModelV2</c>).
/// Returns the configured responses in order and records the requests received.
/// </summary>
public sealed class MockLanguageModel : ILanguageModel
{
    private readonly Queue<Func<LanguageModelRequest, LanguageModelResult>> _responses = new();
    private readonly List<LanguageModelRequest> _requests = [];

    public MockLanguageModel(string modelId = "mock-model", string provider = "mock")
    {
        ModelId = modelId;
        Provider = provider;
    }

    public string Provider { get; }

    public string ModelId { get; }

    /// <summary>Gets the requests received, in order.</summary>
    public IReadOnlyList<LanguageModelRequest> Requests => _requests;

    /// <summary>Gets or sets the size of the text chunks when simulating streaming.</summary>
    public int StreamChunkSize { get; init; } = 4;

    public MockLanguageModel Returns(Func<LanguageModelRequest, LanguageModelResult> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public MockLanguageModel ReturnsText(string text, Usage? usage = null) => Returns(_ => new LanguageModelResult
    {
        Content = [new TextPart(text)],
        FinishReason = FinishReason.Stop,
        Usage = usage ?? new Usage { InputTokens = 10, OutputTokens = 5 },
    });

    public MockLanguageModel ReturnsToolCall(string toolName, object input, string? toolCallId = null) => Returns(_ => new LanguageModelResult
    {
        Content = [new ToolCallPart(toolCallId ?? $"call_{Guid.NewGuid():N}", toolName, JsonSerializer.SerializeToElement(input, JsonSchema.DefaultSerializerOptions))],
        FinishReason = FinishReason.ToolCalls,
        Usage = new Usage { InputTokens = 10, OutputTokens = 5 },
    });

    public MockLanguageModel ReturnsError(Exception error) => Returns(_ => throw error);

    public Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
    {
        lock (_requests)
        {
            _requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("MockLanguageModel: no quedan respuestas configuradas.");
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        await foreach (var part in result.ToStreamParts().ConfigureAwait(false))
        {
            if (part is TextDeltaPart t && StreamChunkSize > 0)
            {
                for (var i = 0; i < t.Delta.Length; i += StreamChunkSize)
                {
                    yield return new TextDeltaPart(t.Id, t.Delta.Substring(i, Math.Min(StreamChunkSize, t.Delta.Length - i)));
                }
            }
            else
            {
                yield return part;
            }
        }
    }
}

/// <summary>Mock embedding model: deterministic vector (FNV-1a hash of the text).</summary>
public sealed class MockEmbeddingModel(int dimensions = 8, int? maxEmbeddingsPerCall = null) : IEmbeddingModel
{
    public string Provider => "mock";

    public string ModelId => "mock-embedding";

    public int? MaxEmbeddingsPerCall => maxEmbeddingsPerCall;

    public int Calls { get; private set; }

    public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        Calls++;
        var embeddings = values.Select(v =>
        {
            var rng = new Random(Fnv1a(v));
            return Enumerable.Range(0, dimensions).Select(_ => (float)rng.NextDouble()).ToArray();
        }).ToList();
        return Task.FromResult(new EmbeddingModelResult(embeddings, new EmbeddingUsage(values.Sum(v => v.Length))));
    }

    private static int Fnv1a(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in value)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }
}
