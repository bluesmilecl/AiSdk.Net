namespace AiSdk;

/// <summary>Archivo generado por un modelo (imagen, audio...).</summary>
public sealed record GeneratedFile(byte[] Data, string MediaType)
{
    public string Base64 => Convert.ToBase64String(Data);

    /// <summary>Guarda el archivo en disco.</summary>
    public Task SaveAsync(string path, CancellationToken cancellationToken = default) => File.WriteAllBytesAsync(path, Data, cancellationToken);
}

// ---------------------------------------------------------------------------
// Imágenes
// ---------------------------------------------------------------------------

/// <summary>Modelo de generación de imágenes (equivalente a <c>ImageModelV2</c>).</summary>
public interface IImageModel
{
    string Provider { get; }

    string ModelId { get; }

    /// <summary>Máximo de imágenes por llamada (null = sin límite conocido).</summary>
    int? MaxImagesPerCall { get; }

    Task<ImageModelResult> GenerateAsync(ImageModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record ImageModelRequest
{
    public required string Prompt { get; init; }

    public int N { get; init; } = 1;

    /// <summary>Tamaño, p. ej. <c>"1024x1024"</c>.</summary>
    public string? Size { get; init; }

    /// <summary>Relación de aspecto, p. ej. <c>"16:9"</c> (para proveedores que la usan en vez de tamaño).</summary>
    public string? AspectRatio { get; init; }

    public int? Seed { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public sealed record ImageModelResult(IReadOnlyList<GeneratedFile> Images)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Prompt reescrito por el proveedor, si lo devuelve.</summary>
    public IReadOnlyList<string?> RevisedPrompts { get; init; } = [];
}

public sealed class GenerateImageOptions
{
    public required IImageModel Model { get; init; }

    public required string Prompt { get; init; }

    public int N { get; init; } = 1;

    public string? Size { get; init; }

    public string? AspectRatio { get; init; }

    public int? Seed { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public int MaxRetries { get; init; } = 2;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

public sealed record GenerateImageResult(IReadOnlyList<GeneratedFile> Images, IReadOnlyList<string> Warnings)
{
    public GeneratedFile Image => Images[0];
}

// ---------------------------------------------------------------------------
// Voz (texto a audio)
// ---------------------------------------------------------------------------

/// <summary>Modelo de síntesis de voz (equivalente a <c>SpeechModelV2</c>).</summary>
public interface ISpeechModel
{
    string Provider { get; }

    string ModelId { get; }

    Task<SpeechModelResult> GenerateAsync(SpeechModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record SpeechModelRequest
{
    public required string Text { get; init; }

    public string? Voice { get; init; }

    /// <summary>Formato de salida, p. ej. <c>"mp3"</c>, <c>"wav"</c>, <c>"opus"</c>.</summary>
    public string? OutputFormat { get; init; }

    /// <summary>Instrucciones de estilo (tono, emoción...), si el modelo las admite.</summary>
    public string? Instructions { get; init; }

    public double? Speed { get; init; }

    public string? Language { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public sealed record SpeechModelResult(GeneratedFile Audio)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class GenerateSpeechOptions
{
    public required ISpeechModel Model { get; init; }

    public required string Text { get; init; }

    public string? Voice { get; init; }

    public string? OutputFormat { get; init; }

    public string? Instructions { get; init; }

    public double? Speed { get; init; }

    public string? Language { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public int MaxRetries { get; init; } = 2;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

public sealed record GenerateSpeechResult(GeneratedFile Audio, IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Transcripción (audio a texto)
// ---------------------------------------------------------------------------

/// <summary>Modelo de transcripción (equivalente a <c>TranscriptionModelV2</c>).</summary>
public interface ITranscriptionModel
{
    string Provider { get; }

    string ModelId { get; }

    Task<TranscriptionModelResult> TranscribeAsync(TranscriptionModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record TranscriptionModelRequest
{
    public required byte[] Audio { get; init; }

    /// <summary>Tipo MIME del audio, p. ej. <c>"audio/mpeg"</c>.</summary>
    public required string MediaType { get; init; }

    public string? Filename { get; init; }

    /// <summary>Idioma ISO-639-1 (p. ej. <c>"es"</c>) para mejorar precisión.</summary>
    public string? Language { get; init; }

    /// <summary>Texto de contexto (vocabulario, nombres propios...).</summary>
    public string? Prompt { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public sealed record TranscriptionSegment(string Text, double StartSeconds, double EndSeconds);

public sealed record TranscriptionModelResult(string Text)
{
    public IReadOnlyList<TranscriptionSegment> Segments { get; init; } = [];

    public string? Language { get; init; }

    public double? DurationSeconds { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class TranscribeOptions
{
    public required ITranscriptionModel Model { get; init; }

    public required byte[] Audio { get; init; }

    public string MediaType { get; init; } = "audio/mpeg";

    public string? Filename { get; init; }

    public string? Language { get; init; }

    public string? Prompt { get; init; }

    public ProviderOptions? ProviderOptions { get; init; }

    public int MaxRetries { get; init; } = 2;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public CancellationToken CancellationToken { get; init; }
}
