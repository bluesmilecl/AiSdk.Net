using System.Runtime.CompilerServices;
using System.Text;

namespace AiSdk;

public static partial class Middlewares
{
    /// <summary>
    /// Extracts the reasoning that some models write inside the text between tags
    /// (e.g. <c>&lt;think&gt;...&lt;/think&gt;</c> in DeepSeek R1, Qwen or other models on Ollama)
    /// and converts it into <see cref="ReasoningPart"/> (equivalent to <c>extractReasoningMiddleware</c>).
    /// </summary>
    /// <param name="tagName">Tag name, without angle brackets.</param>
    /// <param name="startWithReasoning">True if the model starts reasoning without opening the tag.</param>
    public static ILanguageModelMiddleware ExtractReasoning(string tagName = "think", bool startWithReasoning = false) =>
        new ExtractReasoningMiddleware(tagName, startWithReasoning);

    /// <summary>
    /// Simulates streaming for models that only implement complete responses: <c>StreamAsync</c>
    /// calls <c>GenerateAsync</c> and emits the result as a stream.
    /// </summary>
    public static ILanguageModelMiddleware SimulateStreaming() => new SimulateStreamingMiddleware();

    private sealed class SimulateStreamingMiddleware : ILanguageModelMiddleware
    {
        public async IAsyncEnumerable<StreamPart> WrapStream(
            LanguageModelRequest request,
            ILanguageModel model,
            Func<LanguageModelRequest, CancellationToken, IAsyncEnumerable<StreamPart>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var result = await model.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
            await foreach (var part in result.ToStreamParts().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return part;
            }
        }
    }

    private sealed class ExtractReasoningMiddleware(string tagName, bool startWithReasoning) : ILanguageModelMiddleware
    {
        private readonly string _open = $"<{tagName}>";
        private readonly string _close = $"</{tagName}>";

        public async Task<LanguageModelResult> WrapGenerateAsync(
            LanguageModelRequest request,
            ILanguageModel model,
            Func<LanguageModelRequest, CancellationToken, Task<LanguageModelResult>> next,
            CancellationToken cancellationToken)
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            var content = new List<ContentPart>();
            foreach (var part in result.Content)
            {
                if (part is not TextPart text)
                {
                    content.Add(part);
                    continue;
                }

                var (reasoning, remaining) = Split(startWithReasoning ? _open + text.Text : text.Text);
                if (reasoning.Length > 0)
                {
                    content.Add(new ReasoningPart(reasoning));
                }

                if (remaining.Length > 0)
                {
                    content.Add(new TextPart(remaining));
                }
            }

            return result with { Content = content };
        }

        private (string Reasoning, string Text) Split(string text)
        {
            var reasoning = new StringBuilder();
            var output = new StringBuilder();
            var index = 0;
            while (index < text.Length)
            {
                var open = text.IndexOf(_open, index, StringComparison.Ordinal);
                if (open < 0)
                {
                    output.Append(text, index, text.Length - index);
                    break;
                }

                output.Append(text, index, open - index);
                var close = text.IndexOf(_close, open + _open.Length, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close;
                if (reasoning.Length > 0)
                {
                    reasoning.Append('\n');
                }

                reasoning.Append(text, open + _open.Length, end - open - _open.Length);
                index = close < 0 ? text.Length : close + _close.Length;
            }

            return (reasoning.ToString().Trim(), output.ToString().TrimStart());
        }

        public async IAsyncEnumerable<StreamPart> WrapStream(
            LanguageModelRequest request,
            ILanguageModel model,
            Func<LanguageModelRequest, CancellationToken, IAsyncEnumerable<StreamPart>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var inReasoning = startWithReasoning;
            var pending = new StringBuilder(); // texto retenido que podría ser el inicio de una etiqueta
            var reasoningBlock = 0;
            string? textId = null;
            var afterReasoning = false;

            await foreach (var part in next(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (part is not TextDeltaPart delta)
                {
                    yield return part;
                    continue;
                }

                textId = delta.Id;
                pending.Append(delta.Delta);
                while (pending.Length > 0)
                {
                    var tag = inReasoning ? _close : _open;
                    var text = pending.ToString();
                    var found = text.IndexOf(tag, StringComparison.Ordinal);
                    if (found >= 0)
                    {
                        var before = text[..found];
                        foreach (var emitted in Emit(before, inReasoning, delta.Id, reasoningBlock, ref afterReasoning))
                        {
                            yield return emitted;
                        }

                        if (inReasoning)
                        {
                            yield return new ReasoningEndPart($"reasoning-{reasoningBlock}");
                            reasoningBlock++;
                            afterReasoning = true;
                        }

                        inReasoning = !inReasoning;
                        pending.Remove(0, found + tag.Length);
                        continue;
                    }

                    // Retiene un posible prefijo parcial de la etiqueta al final.
                    var keep = PartialTagSuffix(text, tag);
                    var flush = text[..^keep];
                    foreach (var emitted in Emit(flush, inReasoning, delta.Id, reasoningBlock, ref afterReasoning))
                    {
                        yield return emitted;
                    }

                    pending.Remove(0, flush.Length);
                    break;
                }
            }

            if (pending.Length > 0 && textId is not null)
            {
                foreach (var emitted in Emit(pending.ToString(), inReasoning, textId, reasoningBlock, ref afterReasoning))
                {
                    yield return emitted;
                }
            }

            if (inReasoning)
            {
                yield return new ReasoningEndPart($"reasoning-{reasoningBlock}");
            }
        }

        private static List<StreamPart> Emit(string text, bool reasoning, string textId, int block, ref bool afterReasoning)
        {
            if (!reasoning && afterReasoning)
            {
                text = text.TrimStart(); // quita el salto de línea que suele seguir a </think>
                if (text.Length > 0)
                {
                    afterReasoning = false;
                }
            }

            if (text.Length == 0)
            {
                return [];
            }

            return [reasoning ? new ReasoningDeltaPart($"reasoning-{block}", text) : new TextDeltaPart(textId, text)];
        }

        private static int PartialTagSuffix(string text, string tag)
        {
            for (var length = Math.Min(tag.Length - 1, text.Length); length > 0; length--)
            {
                if (text.EndsWith(tag[..length], StringComparison.Ordinal))
                {
                    return length;
                }
            }

            return 0;
        }
    }
}
