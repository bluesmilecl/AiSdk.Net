using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSdk;

/// <summary>Unit by which <see cref="StreamTransforms.Smooth"/> splits the text.</summary>
public enum SmoothChunking
{
    /// <summary>Word by word (includes the following space).</summary>
    Word,

    /// <summary>Line by line.</summary>
    Line,
}

/// <summary>Transforms for <see cref="GenerateTextOptions.StreamTransform"/>.</summary>
public static partial class StreamTransforms
{
    /// <summary>
    /// Smooths text streaming (equivalent to <c>smoothStream()</c>): regroups the chunks
    /// by word or line and waits <paramref name="delay"/> between each one, for uniform output
    /// even when the provider sends large blocks.
    /// </summary>
    public static Func<IAsyncEnumerable<StreamPart>, IAsyncEnumerable<StreamPart>> Smooth(
        TimeSpan? delay = null,
        SmoothChunking chunking = SmoothChunking.Word) =>
        source => SmoothCore(source, delay ?? TimeSpan.FromMilliseconds(10), chunking);

    private static async IAsyncEnumerable<StreamPart> SmoothCore(
        IAsyncEnumerable<StreamPart> source,
        TimeSpan delay,
        SmoothChunking chunking,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new StringBuilder();
        string? bufferId = null;
        var pattern = chunking == SmoothChunking.Word ? WordRegex() : LineRegex();

        await foreach (var part in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (part is TextDeltaPart text)
            {
                if (bufferId is not null && bufferId != text.Id && buffer.Length > 0)
                {
                    yield return new TextDeltaPart(bufferId, buffer.ToString());
                    buffer.Clear();
                }

                bufferId = text.Id;
                buffer.Append(text.Delta);
                while (pattern.Match(buffer.ToString()) is { Success: true } match)
                {
                    var chunk = buffer.ToString(0, match.Index + match.Length);
                    buffer.Remove(0, chunk.Length);
                    yield return new TextDeltaPart(text.Id, chunk);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                continue;
            }

            // Cualquier otra parte vacía el texto pendiente para conservar el orden.
            if (buffer.Length > 0 && bufferId is not null)
            {
                yield return new TextDeltaPart(bufferId, buffer.ToString());
                buffer.Clear();
            }

            yield return part;
        }

        if (buffer.Length > 0 && bufferId is not null)
        {
            yield return new TextDeltaPart(bufferId, buffer.ToString());
        }
    }

    [GeneratedRegex(@"\S+\s+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\n+")]
    private static partial Regex LineRegex();
}
