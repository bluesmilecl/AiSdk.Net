using System.Text;
using System.Text.Json;

namespace AiSdk;

/// <summary>
/// Fast token estimation without a tokenizer (≈ 4 characters per token in Latin-script languages,
/// ≈ 1 per character in CJK). Good enough for budgets; for exact figures use
/// <see cref="Ai.CountTokensAsync(GenerateTextOptions)"/>.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Tokens assumed for each attached image or binary file.</summary>
    public const int AttachmentTokens = 1000;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= '鿿' or >= '가' and <= '힯')
            {
                cjk++;
            }
        }

        return cjk + (int)Math.Ceiling((text.Length - cjk) / 4.0);
    }

    public static int Estimate(ModelMessage message) => 4 + message.Content.Sum(Estimate);

    public static int Estimate(IEnumerable<ModelMessage> messages) => messages.Sum(Estimate);

    public static int Estimate(ContentPart part) => part switch
    {
        TextPart t => Estimate(t.Text),
        ReasoningPart r => Estimate(r.Text),
        ToolCallPart c => 8 + Estimate(c.Input.GetRawText()),
        ToolResultPart r => 8 + Estimate(r.Output.GetRawText()),
        ImagePart or FilePart => AttachmentTokens,
        _ => 10,
    };
}

/// <summary>Options for <see cref="Middlewares.CompressContext(ContextCompressionOptions)"/>.</summary>
public sealed class ContextCompressionOptions
{
    /// <summary>Gets or sets the input token budget. Above this value, the context is compressed.</summary>
    public required int MaxInputTokens { get; init; }

    /// <summary>Gets or sets the number of recent messages that are always sent in full.</summary>
    public int KeepRecentMessages { get; init; } = 6;

    /// <summary>
    /// Gets or sets the model that summarizes old messages (a small, cheap one is usually enough).
    /// If null, old messages are discarded.
    /// </summary>
    public ILanguageModel? Summarizer { get; init; }

    /// <summary>Gets or sets the summary instructions.</summary>
    public string SummaryInstructions { get; init; } =
        "Resume la conversación anterior para que un asistente pueda continuarla sin perder contexto. " +
        "Conserva datos concretos (nombres, cifras, fechas, ids, decisiones, preferencias del usuario, " +
        "resultados de herramientas y tareas pendientes). Escribe en el idioma de la conversación, en viñetas concisas.";

    /// <summary>Gets or sets the maximum size (in tokens) of old tool results before they are trimmed.</summary>
    public int MaxToolResultTokens { get; init; } = 500;

    /// <summary>Gets or sets the counting function. Defaults to <see cref="TokenEstimator"/>.</summary>
    public Func<IReadOnlyList<ModelMessage>, int>? CountTokens { get; init; }
}

/// <summary>Tools for reducing long text before sending it to a model.</summary>
public static class ContextCompressor
{
    /// <summary>
    /// Extractive compression: splits the text, scores each chunk by similarity to
    /// <paramref name="query"/> and keeps the most relevant ones (in their original order) up to
    /// <paramref name="maxTokens"/>. It does not use a language model, only embeddings.
    /// </summary>
    public static async Task<string> SelectRelevantAsync(
        IEmbeddingModel embeddings,
        string text,
        string query,
        int maxTokens,
        int chunkChars = 800,
        CancellationToken cancellationToken = default)
    {
        if (TokenEstimator.Estimate(text) <= maxTokens)
        {
            return text;
        }

        // Fragmentos lo bastante pequeños como para que quepan varios en el presupuesto.
        var chunks = TextChunker.Split(text, Math.Min(chunkChars, Math.Max(200, maxTokens * 2)), overlap: 0);
        var queryEmbedding = (await Ai.EmbedAsync(embeddings, query, cancellationToken).ConfigureAwait(false)).Embedding;
        var chunkEmbeddings = (await Ai.EmbedManyAsync(embeddings, chunks, cancellationToken).ConfigureAwait(false)).Embeddings;

        var selected = new SortedSet<int>();
        var used = 0;
        foreach (var (index, _) in chunkEmbeddings
                     .Select((e, i) => (Index: i, Score: Ai.CosineSimilarity(queryEmbedding, e)))
                     .OrderByDescending(x => x.Score))
        {
            var cost = TokenEstimator.Estimate(chunks[index]);
            if (used + cost > maxTokens)
            {
                continue;
            }

            selected.Add(index);
            used += cost;
        }

        if (selected.Count == 0)
        {
            // Ningún fragmento cabe completo: se recorta el más relevante.
            var best = chunkEmbeddings.Select((e, i) => (Index: i, Score: Ai.CosineSimilarity(queryEmbedding, e))).MaxBy(x => x.Score).Index;
            return chunks[best][..Math.Min(chunks[best].Length, maxTokens * 4)] + "…";
        }

        var builder = new StringBuilder();
        var previous = -1;
        foreach (var index in selected)
        {
            if (builder.Length > 0)
            {
                builder.Append(index == previous + 1 ? "\n\n" : "\n\n[…]\n\n");
            }

            builder.Append(chunks[index]);
            previous = index;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Abstractive compression (map-reduce): summarizes in parts and then combines the summaries
    /// until the result is close to <paramref name="targetTokens"/>. Works with texts larger
    /// than the model's context window.
    /// </summary>
    /// <remarks><c>focus</c> parameter: optional, what to preserve (e.g. "payment clauses and deadlines").</remarks>
    public static async Task<string> SummarizeAsync(
        ILanguageModel model,
        string text,
        int targetTokens,
        string? focus = null,
        int chunkTokens = 8000,
        CancellationToken cancellationToken = default)
    {
        if (TokenEstimator.Estimate(text) <= targetTokens)
        {
            return text;
        }

        var current = text;
        for (var round = 0; round < 6 && TokenEstimator.Estimate(current) > targetTokens; round++)
        {
            var parts = TextChunker.Split(current, chunkTokens * 4, overlap: 0);
            var perPart = Math.Max(150, targetTokens / parts.Count);
            var summaries = new string[parts.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, parts.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (i, ct) => summaries[i] = await SummarizeOnceAsync(model, parts[i], perPart, focus, ct).ConfigureAwait(false)).ConfigureAwait(false);
            current = string.Join("\n\n", summaries);
            if (parts.Count == 1)
            {
                break;
            }
        }

        return current;
    }

    private static async Task<string> SummarizeOnceAsync(ILanguageModel model, string text, int maxTokens, string? focus, CancellationToken cancellationToken)
    {
        var instructions = $"Resume el siguiente texto en Markdown en un máximo aproximado de {maxTokens} tokens. " +
                           "Conserva cifras, nombres, fechas, definiciones y conclusiones; no inventes nada. Responde solo con el resumen." +
                           (focus is null ? string.Empty : $" Prioriza: {focus}.");
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            System = instructions,
            Prompt = text,
            MaxOutputTokens = Math.Max(256, maxTokens * 2),
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        return result.Text.Trim();
    }
}

public static partial class Middlewares
{
    /// <summary>
    /// Keeps the conversation within a token budget: trims old tool results
    /// and, if that is not enough, summarizes (or discards) old messages while keeping the recent ones.
    /// It never separates a tool call from its result. Summaries are cached and
    /// updated incrementally between turns.
    /// </summary>
    public static ILanguageModelMiddleware CompressContext(ContextCompressionOptions options) => new ContextCompressionMiddleware(options);

    /// <inheritdoc cref="CompressContext(ContextCompressionOptions)"/>
    public static ILanguageModelMiddleware CompressContext(int maxInputTokens, ILanguageModel? summarizer = null, int keepRecentMessages = 6) =>
        new ContextCompressionMiddleware(new ContextCompressionOptions
        {
            MaxInputTokens = maxInputTokens,
            Summarizer = summarizer,
            KeepRecentMessages = keepRecentMessages,
        });
}

internal sealed class ContextCompressionMiddleware(ContextCompressionOptions options) : ILanguageModelMiddleware
{
    private readonly Lock _gate = new();
    private (string Key, int Count, string Summary)? _lastSummary;

    private int Count(IReadOnlyList<ModelMessage> messages) =>
        options.CountTokens?.Invoke(messages) ?? TokenEstimator.Estimate(messages);

    public async ValueTask<LanguageModelRequest> TransformRequestAsync(LanguageModelRequest request, ILanguageModel model, CancellationToken cancellationToken)
    {
        var messages = request.Messages;
        if (Count(messages) <= options.MaxInputTokens)
        {
            return request;
        }

        var system = messages.TakeWhile(m => m.Role == ChatRole.System).ToList();
        var rest = messages.Skip(system.Count).ToList();
        var recentStart = SafeBoundary(rest, Math.Max(0, rest.Count - options.KeepRecentMessages));

        // 1) Recortar resultados de herramientas antiguos.
        var old = rest.Take(recentStart).Select(TrimToolResults).ToList();
        var recent = rest.Skip(recentStart).ToList();
        var candidate = system.Concat(old).Concat(recent).ToList();
        if (Count(candidate) <= options.MaxInputTokens)
        {
            return request with { Messages = candidate };
        }

        // 2) Elegir la ventana reciente que cabe dejando espacio para el resumen; luego resumir una sola vez.
        var reserve = options.Summarizer is null ? 50 : SummaryBudget;
        while (recentStart < rest.Count - 1 && Count([.. system, .. rest.Skip(recentStart)]) + reserve > options.MaxInputTokens)
        {
            // Avanza el límite saltando resultados de herramientas para no dejarlos huérfanos.
            recentStart++;
            while (recentStart < rest.Count - 1 && rest[recentStart].Role == ChatRole.Tool)
            {
                recentStart++;
            }
        }

        var older = rest.Take(recentStart).ToList();
        var note = older.Count == 0
            ? null
            : options.Summarizer is null
                ? $"[Se omitieron {older.Count} mensajes anteriores de la conversación para ajustarse al contexto.]"
                : "Resumen de la conversación anterior:\n" + await SummarizeAsync(older, cancellationToken).ConfigureAwait(false);

        return request with { Messages = [.. system, .. note is null ? [] : new[] { ModelMessage.System(note) }, .. rest.Skip(recentStart)] };
    }

    private int SummaryBudget => Math.Clamp(options.MaxInputTokens / 4, 128, 2000);

    /// <summary>Moves the boundary back so as not to start with orphaned tool results.</summary>
    private static int SafeBoundary(List<ModelMessage> messages, int index)
    {
        index = Math.Clamp(index, 0, messages.Count);
        while (index > 0 && index < messages.Count && messages[index].Role == ChatRole.Tool)
        {
            index--;
        }

        return index;
    }

    private ModelMessage TrimToolResults(ModelMessage message)
    {
        if (message.Role != ChatRole.Tool)
        {
            return message;
        }

        var maxChars = options.MaxToolResultTokens * 4;
        return message with
        {
            Content = message.Content.Select(part => part is ToolResultPart r && r.Output.GetRawText().Length > maxChars
                ? r with { Output = JsonSerializer.SerializeToElement(r.Output.GetRawText()[..maxChars] + "… [resultado recortado]") }
                : part).ToList(),
        };
    }

    private async Task<string> SummarizeAsync(List<ModelMessage> older, CancellationToken cancellationToken)
    {
        var key = Hash(older);
        (string Key, int Count, string Summary)? previous;
        lock (_gate)
        {
            previous = _lastSummary;
        }

        if (previous is { } p && p.Key == key)
        {
            return p.Summary;
        }

        // Incremental: si ya resumimos un prefijo, solo se agregan los mensajes nuevos.
        string input;
        if (previous is { } q && q.Count < older.Count && Hash(older.Take(q.Count)) == q.Key)
        {
            input = $"Resumen previo:\n{q.Summary}\n\nMensajes nuevos:\n{Render(older.Skip(q.Count))}";
        }
        else
        {
            input = Render(older);
        }

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = options.Summarizer!,
            System = options.SummaryInstructions,
            Prompt = input,
            MaxOutputTokens = SummaryBudget,
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);

        var summary = result.Text.Trim();
        lock (_gate)
        {
            _lastSummary = (key, older.Count, summary);
        }

        return summary;
    }

    private static string Render(IEnumerable<ModelMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            foreach (var part in message.Content)
            {
                var text = part switch
                {
                    TextPart t => t.Text,
                    ToolCallPart c => $"[llamada a {c.ToolName}: {Truncate(c.Input.GetRawText(), 1500)}]",
                    ToolResultPart r => $"[resultado de {r.ToolName}: {Truncate(r.Output.GetRawText(), 1500)}]",
                    ImagePart => "[imagen]",
                    FilePart f => $"[archivo {f.Filename ?? f.MediaType}]",
                    _ => null,
                };
                if (text is not null)
                {
                    builder.Append(message.Role.ToString().ToLowerInvariant()).Append(": ").AppendLine(text);
                }
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Hash(IEnumerable<ModelMessage> messages) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(messages.ToList(), JsonSchema.DefaultSerializerOptions)));
}
