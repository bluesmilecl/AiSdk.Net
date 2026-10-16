using System.Text;
using System.Text.Json;

namespace AiSdk.Internal;

/// <summary>Reconstruye un <see cref="LanguageModelResult"/> a partir de las partes de un stream.</summary>
public sealed class StreamAccumulator
{
    private readonly List<object> _blocks = [];
    private readonly Dictionary<string, Block> _text = [];
    private readonly Dictionary<string, Block> _reasoning = [];

    public FinishReason FinishReason { get; private set; } = FinishReason.Unknown;

    public Usage Usage { get; private set; } = Usage.Empty;

    public ResponseMetadata? Response { get; private set; }

    public void Add(StreamPart part)
    {
        switch (part)
        {
            case TextDeltaPart t:
                GetOrAdd(_text, t.Id).Builder.Append(t.Delta);
                break;
            case ReasoningDeltaPart r:
                GetOrAdd(_reasoning, r.Id).Builder.Append(r.Delta);
                break;
            case ReasoningEndPart e:
                GetOrAdd(_reasoning, e.Id).Metadata = e.ProviderMetadata;
                break;
            case ToolCallStreamPart c:
                _blocks.Add(c.ToolCall);
                break;
            case ResponseMetadataPart m:
                Response = m.Metadata;
                break;
            case ModelFinishPart f:
                FinishReason = f.FinishReason;
                Usage = f.Usage;
                break;
        }
    }

    public IReadOnlyList<ContentPart> BuildContent()
    {
        var content = new List<ContentPart>();
        foreach (var block in _blocks)
        {
            switch (block)
            {
                case Block { IsReasoning: true } r:
                    content.Add(new ReasoningPart(r.Builder.ToString()) { ProviderMetadata = r.Metadata });
                    break;
                case Block t:
                    if (t.Builder.Length > 0)
                    {
                        content.Add(new TextPart(t.Builder.ToString()));
                    }

                    break;
                case ToolCallPart call:
                    content.Add(call);
                    break;
            }
        }

        return content;
    }

    public LanguageModelResult Build() => new()
    {
        Content = BuildContent(),
        FinishReason = FinishReason,
        Usage = Usage,
        Response = Response,
    };

    private Block GetOrAdd(Dictionary<string, Block> map, string id)
    {
        if (!map.TryGetValue(id, out var block))
        {
            block = new Block { IsReasoning = ReferenceEquals(map, _reasoning) };
            map[id] = block;
            _blocks.Add(block);
        }

        return block;
    }

    private sealed class Block
    {
        public StringBuilder Builder { get; } = new();

        public bool IsReasoning { get; init; }

        public IReadOnlyDictionary<string, JsonElement>? Metadata { get; set; }
    }
}
