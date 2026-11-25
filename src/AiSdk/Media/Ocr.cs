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
    public const string DefaultInstructions =
        "Transcribe todo el texto visible de la imagen a Markdown, respetando el orden de lectura. " +
        "Usa encabezados, listas y tablas Markdown cuando correspondan. No traduzcas, no resumas ni agregues comentarios. " +
        "Si no hay texto, responde exactamente: (sin texto)";

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
        return new OcrResult([new OcrPage(1, text == "(sin texto)" ? string.Empty : text)]);
    }
}
