using AiSdk.Resources;
namespace AiSdk;

/// <summary>
/// Optical character recognition engine. Converts images (and, if the engine supports it,
/// scanned PDFs) into Markdown text that any model can read.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Gets a descriptive name (e.g. <c>"tesseract"</c>, <c>"mistral-ocr-latest"</c>).</summary>
    string Name { get; }

    /// <summary>Indicates whether the engine directly accepts this MIME type (images should always be supported).</summary>
    bool Supports(string mediaType);

    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default);
}

public sealed record OcrRequest
{
    public required byte[] Data { get; init; }

    /// <summary>Gets or sets the MIME type, e.g. <c>"image/png"</c> or <c>"application/pdf"</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// Gets or sets the expected ISO-639-1 language (e.g. <c>"es"</c>); it improves the accuracy of some engines.
    /// </summary>
    public string? Language { get; init; }
}

/// <summary>Recognized page. <see cref="Confidence"/> ranges from 0 to 1 when the engine reports it.</summary>
public sealed record OcrPage(int Number, string Markdown)
{
    public double? Confidence { get; init; }
}

public sealed record OcrResult(IReadOnlyList<OcrPage> Pages)
{
    /// <summary>Gets the text of all the pages, separated by blank lines.</summary>
    public string Markdown => string.Join("\n\n", Pages.Select(p => p.Markdown.Trim()).Where(t => t.Length > 0));
}

/// <summary>
/// OCR through a vision model (Claude, GPT, Gemini...). It reads tables, handwriting and
/// complex layouts well, and returns structured Markdown.
/// </summary>
public sealed class VisionModelOcr(ILanguageModel model, string? instructions = null) : IOcrEngine
{
    /// <summary>
    /// Gets the default transcription instructions, in the language of <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.
    /// They ask the model to reply exactly <c>(no text)</c> (or <c>(sin texto)</c> in Spanish) when the image has no text.
    /// </summary>
    public static string DefaultInstructions => Strings.Ocr_DefaultInstructions;

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="reply"/> is the "no text" answer the default instructions ask
    /// for, in any of the supported languages (<c>(no text)</c>, <c>(sin texto)</c>).
    /// </summary>
    /// <param name="reply">The model's reply.</param>
    public static bool IsNoTextReply(string? reply) =>
        reply?.Trim() is { } trimmed
        && (trimmed.Equals("(no text)", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("(sin texto)", StringComparison.OrdinalIgnoreCase));

    public string Name => $"{model.Provider}:{model.ModelId}";

    public bool Supports(string mediaType) => mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Messages =
            [
                ModelMessage.User(
                    new TextPart(instructions ?? DefaultInstructions),
                    ImagePart.FromBytes(request.Data, request.MediaType)),
            ],
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        var text = result.Text.Trim();
        return new OcrResult([new OcrPage(1, IsNoTextReply(text) ? string.Empty : text)]);
    }
}
