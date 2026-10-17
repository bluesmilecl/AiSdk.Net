namespace AiSdk;

/// <summary>
/// Motor de reconocimiento óptico de caracteres. Convierte imágenes (y, si el motor lo admite,
/// PDF escaneados) en texto Markdown que cualquier modelo puede leer.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Nombre descriptivo (p. ej. <c>"tesseract"</c>, <c>"mistral-ocr-latest"</c>).</summary>
    string Name { get; }

    /// <summary>Indica si el motor acepta directamente este tipo MIME (las imágenes siempre deberían estarlo).</summary>
    bool Supports(string mediaType);

    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default);
}

public sealed record OcrRequest
{
    public required byte[] Data { get; init; }

    /// <summary>Tipo MIME, p. ej. <c>"image/png"</c> o <c>"application/pdf"</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>Idioma ISO-639-1 esperado (p. ej. <c>"es"</c>); mejora la precisión de algunos motores.</summary>
    public string? Language { get; init; }
}

/// <summary>Página reconocida. <see cref="Confidence"/> va de 0 a 1 cuando el motor la informa.</summary>
public sealed record OcrPage(int Number, string Markdown)
{
    public double? Confidence { get; init; }
}

public sealed record OcrResult(IReadOnlyList<OcrPage> Pages)
{
    /// <summary>Texto de todas las páginas separado por líneas en blanco.</summary>
    public string Markdown => string.Join("\n\n", Pages.Select(p => p.Markdown.Trim()).Where(t => t.Length > 0));
}

/// <summary>
/// OCR mediante un modelo con visión (Claude, GPT, Gemini...). Lee bien tablas, escritura a mano y
/// diseños complejos, y devuelve Markdown estructurado.
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
