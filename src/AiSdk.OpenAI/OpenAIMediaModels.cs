using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Internal;

namespace AiSdk.OpenAI;

/// <summary>Generación de imágenes (<c>/images/generations</c>): <c>gpt-image-1</c>, <c>dall-e-3</c>...</summary>
public sealed class OpenAIImageModel : IImageModel
{
    private readonly OpenAIProvider _provider;

    internal OpenAIImageModel(string modelId, OpenAIProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    /// <summary>dall-e-3 solo admite una imagen por llamada.</summary>
    public int? MaxImagesPerCall => ModelId.StartsWith("dall-e-3", StringComparison.Ordinal) ? 1 : 10;

    public async Task<ImageModelResult> GenerateAsync(ImageModelRequest request, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
        };
        if (request.Size is not null)
        {
            body["size"] = request.Size;
        }

        if (request.AspectRatio is not null)
        {
            warnings.Add("OpenAI no admite AspectRatio; usa Size (p. ej. \"1536x1024\").");
        }

        if (request.Seed is not null)
        {
            warnings.Add("OpenAI no admite Seed en imágenes; se ignoró.");
        }

        // Los modelos DALL·E devuelven URL por defecto; se piden en base64. gpt-image-* siempre devuelve base64.
        if (ModelId.StartsWith("dall-e", StringComparison.Ordinal))
        {
            body["response_format"] = "b64_json";
        }

        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("openai"));

        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("images/generations"), body, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        var mediaType = (string?)body["output_format"] switch
        {
            "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png",
        };
        var data = (json["data"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var images = data
            .Where(d => (string?)d["b64_json"] is not null)
            .Select(d => new GeneratedFile(Convert.FromBase64String((string)d["b64_json"]!), mediaType))
            .ToList();

        return new ImageModelResult(images)
        {
            Warnings = warnings,
            RevisedPrompts = data.Select(d => (string?)d["revised_prompt"]).ToList(),
        };
    }
}

/// <summary>Texto a voz (<c>/audio/speech</c>): <c>gpt-4o-mini-tts</c>, <c>tts-1</c>...</summary>
public sealed class OpenAISpeechModel : ISpeechModel
{
    private readonly OpenAIProvider _provider;

    internal OpenAISpeechModel(string modelId, OpenAIProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<SpeechModelResult> GenerateAsync(SpeechModelRequest request, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var format = request.OutputFormat ?? "mp3";
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["input"] = request.Text,
            ["voice"] = request.Voice ?? "alloy",
            ["response_format"] = format,
        };
        if (request.Instructions is not null)
        {
            body["instructions"] = request.Instructions;
        }

        if (request.Speed is { } speed)
        {
            body["speed"] = speed;
        }

        if (request.Language is not null)
        {
            warnings.Add("OpenAI detecta el idioma del texto automáticamente; Language se ignoró.");
        }

        JsonUtil.DeepMerge(body, request.ProviderOptions?.For("openai"));

        using var http = ProviderHttp.CreateJsonRequest(_provider.Url("audio/speech"), body, _provider.Headers(request.Headers), stream: false);
        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return new SpeechModelResult(new GeneratedFile(bytes, AudioMediaType(format))) { Warnings = warnings };
    }

    internal static string AudioMediaType(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "pcm" => "audio/pcm",
        _ => "application/octet-stream",
    };
}

/// <summary>Transcripción (<c>/audio/transcriptions</c>): <c>gpt-4o-transcribe</c>, <c>whisper-1</c>...</summary>
public sealed class OpenAITranscriptionModel : ITranscriptionModel
{
    private readonly OpenAIProvider _provider;

    internal OpenAITranscriptionModel(string modelId, OpenAIProvider provider)
    {
        ModelId = modelId;
        _provider = provider;
    }

    public string Provider => _provider.Name;

    public string ModelId { get; }

    public async Task<TranscriptionModelResult> TranscribeAsync(TranscriptionModelRequest request, CancellationToken cancellationToken = default)
    {
        // whisper-1 devuelve segmentos con tiempos en verbose_json; los modelos gpt-4o-*-transcribe solo admiten json.
        var verbose = ModelId.StartsWith("whisper", StringComparison.Ordinal);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(request.Audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", request.Filename ?? "audio" + Extension(request.MediaType));
        form.Add(new StringContent(ModelId), "model");
        form.Add(new StringContent(verbose ? "verbose_json" : "json"), "response_format");
        if (verbose)
        {
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
        }

        if (request.Language is not null)
        {
            form.Add(new StringContent(request.Language), "language");
        }

        if (request.Prompt is not null)
        {
            form.Add(new StringContent(request.Prompt), "prompt");
        }

        foreach (var (key, value) in request.ProviderOptions?.For("openai") ?? [])
        {
            form.Add(new StringContent(value is JsonValue v && v.TryGetValue<string>(out var str) ? str : value?.ToJsonString() ?? string.Empty), key);
        }

        using var http = new HttpRequestMessage(HttpMethod.Post, _provider.Url("audio/transcriptions")) { Content = form };
        foreach (var (key, value) in _provider.Headers(request.Headers))
        {
            http.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await ProviderHttp.SendAsync(_provider.Http, http, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        return new TranscriptionModelResult((string?)json["text"] ?? string.Empty)
        {
            Language = (string?)json["language"],
            DurationSeconds = (double?)json["duration"],
            Segments = (json["segments"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(s => new TranscriptionSegment(((string?)s["text"] ?? string.Empty).Trim(), (double?)s["start"] ?? 0, (double?)s["end"] ?? 0))
                .ToList(),
        };
    }

    private static string Extension(string mediaType) => mediaType switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/webm" => ".webm",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        _ => ".bin",
    };
}
