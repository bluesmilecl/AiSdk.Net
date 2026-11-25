using System.Globalization;
using System.Text;

namespace AiSdk.Documents.Converters;

/// <summary>
/// Images to Markdown: text recognized by OCR and/or a description from a vision model. Lets models without vision
/// "see" screenshots, photos of documents, charts and diagrams.
/// </summary>
public sealed class ImageConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => MediaTypes.IsImage(mediaType);

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var name = source.FileName ?? "imagen";
        var warnings = new List<string>();
        var ocrTask = context.OcrAsync(source.Data, source.MediaType, cancellationToken);
        var descriptionTask = context.DescribeImageAsync(source.Data, source.MediaType, null, cancellationToken);
        var ocr = await ocrTask.ConfigureAwait(false);
        var description = await descriptionTask.ConfigureAwait(false);

        var builder = new StringBuilder($"## Imagen: {name}\n\n");
        var kind = PageTextSource.Text;
        if (description is { Length: > 0 })
        {
            builder.Append(description).Append("\n\n");
            kind = PageTextSource.Vision;
        }

        if (ocr is { Markdown.Length: > 0 } && (description is null || !Contains(description, ocr.Markdown)))
        {
            builder.Append(description is null ? string.Empty : "**Texto detectado (OCR):**\n\n").Append(ocr.Markdown).Append("\n\n");
            kind = kind == PageTextSource.Text ? PageTextSource.Ocr : kind;
        }

        if (context.OversizedImagesSkipped > 0)
        {
            warnings.Add(context.OversizedImageWarning()!);
        }
        else if (ocr is null && description is null)
        {
            warnings.Add("No hay Ocr ni VisionModel configurado: la imagen no se pudo analizar.");
        }

        var markdown = builder.ToString().Trim();
        return new DocumentConversionResult
        {
            Markdown = markdown,
            MediaType = source.MediaType,
            FileName = source.FileName,
            Pages = [new DocumentPage(1, markdown, kind)],
            Warnings = warnings,
        };
    }

    /// <summary>True if the description already includes almost all of the OCR text (avoids duplicating tokens).</summary>
    private static bool Contains(string description, string ocr)
    {
        var words = ocr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToList();
        return words.Count > 0 && words.Count(w => description.Contains(w, StringComparison.OrdinalIgnoreCase)) >= words.Count * 0.8;
    }
}

/// <summary>Audio to Markdown through an <see cref="ITranscriptionModel"/>, with timestamps if available.</summary>
public sealed class AudioConverter : IDocumentConverter
{
    public bool CanConvert(string mediaType) => MediaTypes.IsAudio(mediaType);

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Options.Transcription is null)
        {
            return new DocumentConversionResult
            {
                Markdown = $"[Audio: {source.FileName ?? "audio"}]",
                MediaType = source.MediaType,
                FileName = source.FileName,
                Warnings = ["No hay modelo de transcripción configurado (Transcription)."],
            };
        }

        // Con ffmpeg se mide la duración y, si supera MaxMediaDuration, solo se transcribe ese tramo (la transcripción
        // suele cobrarse por minuto); sin ffmpeg se envía tal cual (solo acota MaxInputBytes).
        var warnings = new List<string>();
        var (audio, mediaType, fileName) = (source.Data, source.MediaType, source.FileName);
        var options = context.Options;
        var runner = options.ProcessRunner ?? ExternalProcess.Runner;
        if (runner.Exists(options.FfmpegPath))
        {
            using var temp = new TempDirectory();
            var input = temp.File("input" + MediaTypes.ExtensionFor(source.MediaType));
            await File.WriteAllBytesAsync(input, source.Data, cancellationToken).ConfigureAwait(false);
            var maxSeconds = Math.Max(1, options.MaxMediaDuration.TotalSeconds);
            if (await VideoConverter.ProbeDurationAsync(runner, options.FfmpegPath, input, cancellationToken).ConfigureAwait(false) is { } duration
                && duration > maxSeconds)
            {
                var clip = temp.File("audio.mp3");
                var cut = await runner.RunAsync(
                    options.FfmpegPath,
                    ["-nostdin", "-v", "error", .. VideoConverter.SafeInputArguments, "-t", maxSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
                     "-vn", "-ac", "1", "-ar", "16000", "-b:a", "48k", "-f", "mp3", clip],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (cut.ExitCode != 0 || !File.Exists(clip))
                {
                    throw new InvalidDataException($"El audio dura {MediaHelpers.Timestamp(duration)} (más que MaxMediaDuration) y no se pudo recortar: {cut.Error.Trim()}");
                }

                (audio, mediaType, fileName) = (await File.ReadAllBytesAsync(clip, cancellationToken).ConfigureAwait(false), "audio/mpeg", "audio.mp3");
                warnings.Add($"El audio dura {MediaHelpers.Timestamp(duration)}: solo se transcribieron los primeros {MediaHelpers.Timestamp(maxSeconds)} (MaxMediaDuration).");
            }
        }

        var transcript = await MediaHelpers.TranscribeAsync(context, audio, mediaType, fileName, cancellationToken).ConfigureAwait(false);
        var markdown = $"## Transcripción: {source.FileName ?? "audio"}\n\n{MediaHelpers.FormatTranscript(transcript)}";
        return new DocumentConversionResult
        {
            Markdown = markdown,
            MediaType = source.MediaType,
            FileName = source.FileName,
            Pages = [new DocumentPage(1, markdown, PageTextSource.Transcript)],
            Warnings = warnings,
        };
    }
}

/// <summary>
/// Video to Markdown with <c>ffmpeg</c>: transcribes the audio and describes frames at regular intervals, producing a
/// timeline that a model without video support can read. ffmpeg and ffprobe can only read the local file
/// (<c>-protocol_whitelist file</c>) with audio and video formats (<c>-format_whitelist</c>: no HLS playlists or
/// <c>concat</c>, which could read URLs or other files), and only the first
/// <see cref="MarkdownConverterOptions.MaxMediaDuration"/>.
/// </summary>
public sealed class VideoConverter : IDocumentConverter
{
    /// <summary>Demuxers ffmpeg can use with an attached video.</summary>
    public const string AllowedFormats = "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,asf,flv,mpegts,mpeg,ogg,mp3,wav,aac,flac,m4v,h264,hevc";

    public const string FramePrompt =
        "Este es un fotograma de un video. Describe en una o dos frases la escena, personas, acciones y cualquier texto visible " +
        "(diapositivas, subtítulos, pantallas). En el idioma del texto visible o en español.";

    public bool CanConvert(string mediaType) => MediaTypes.IsVideo(mediaType);

    public async Task<DocumentConversionResult> ConvertAsync(DocumentSource source, DocumentConversionContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options;
        var runner = options.ProcessRunner ?? ExternalProcess.Runner;
        var warnings = new List<string>();
        if (!runner.Exists(options.FfmpegPath))
        {
            return new DocumentConversionResult
            {
                Markdown = $"[Video: {source.FileName ?? "video"}]",
                MediaType = source.MediaType,
                FileName = source.FileName,
                Warnings = ["ffmpeg no está instalado: no se puede analizar el video."],
            };
        }

        using var temp = new TempDirectory();
        var input = temp.File("input" + MediaTypes.ExtensionFor(source.MediaType));
        await File.WriteAllBytesAsync(input, source.Data, cancellationToken).ConfigureAwait(false);
        var duration = await ProbeDurationAsync(runner, options.FfmpegPath, input, cancellationToken).ConfigureAwait(false);
        var maxSeconds = Math.Max(1, options.MaxMediaDuration.TotalSeconds);
        if (duration > maxSeconds)
        {
            warnings.Add($"El video dura {MediaHelpers.Timestamp(duration.Value)}: solo se analizaron los primeros {MediaHelpers.Timestamp(maxSeconds)} (MaxMediaDuration).");
        }

        // Opciones de entrada: solo el archivo local, formatos de video y, como máximo, MaxMediaDuration.
        string[] safeInput = [.. SafeInputArguments, "-t", maxSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input];
        var interval = options.VideoFrameInterval.TotalSeconds;
        if (duration is { } d && Math.Min(d, maxSeconds) / interval > options.MaxVideoFrames)
        {
            interval = Math.Ceiling(Math.Min(d, maxSeconds) / options.MaxVideoFrames);
        }

        // Audio → transcripción.
        TranscriptionModelResult? transcript = null;
        if (options.Transcription is null)
        {
            warnings.Add("Sin Transcription: no se transcribe el audio del video.");
        }
        else
        {
            var audioPath = temp.File("audio.mp3");
            var audio = await runner.RunAsync(
                options.FfmpegPath,
                ["-nostdin", "-v", "error", .. safeInput, "-vn", "-ac", "1", "-ar", "16000", "-b:a", "48k", "-f", "mp3", audioPath],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (audio.ExitCode == 0 && File.Exists(audioPath) && new FileInfo(audioPath).Length > 0)
            {
                var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
                transcript = await MediaHelpers.TranscribeAsync(context, bytes, "audio/mpeg", "audio.mp3", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                warnings.Add("El video no tiene pista de audio o no se pudo extraer.");
            }
        }

        // Fotogramas → descripción u OCR.
        var frames = new List<(double Time, string Text)>();
        if (context.CanAnalyzeImages)
        {
            var framesDir = Directory.CreateDirectory(temp.File("frames")).FullName;
            // Un fotograma en t=0 y luego uno cada `interval` segundos (marcas de tiempo exactas).
            var step = interval.ToString("0.###", CultureInfo.InvariantCulture);
            await runner.RunCheckedAsync(
                options.FfmpegPath,
                ["-nostdin", "-v", "error", .. safeInput, "-vf", $"select='isnan(prev_selected_t)+gte(t-prev_selected_t\\,{step})',scale='min(1024,iw)':-2", "-fps_mode", "vfr", "-frames:v", options.MaxVideoFrames.ToString(CultureInfo.InvariantCulture), "-q:v", "4", Path.Combine(framesDir, "f_%04d.jpg")],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var files = Directory.GetFiles(framesDir, "f_*.jpg").Order(StringComparer.Ordinal).ToList();
            var results = new string?[files.Count];
            using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
            await Task.WhenAll(files.Select(async (file, i) =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                    results[i] = options.VisionModel is not null
                        ? await context.DescribeImageAsync(bytes, "image/jpeg", FramePrompt, cancellationToken).ConfigureAwait(false)
                        : (await context.OcrAsync(bytes, "image/jpeg", cancellationToken).ConfigureAwait(false))?.Markdown;
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            string? previous = null;
            for (var i = 0; i < results.Length; i++)
            {
                var text = results[i]?.Trim();
                if (string.IsNullOrEmpty(text) || text == previous)
                {
                    continue;
                }

                frames.Add((i * interval, text));
                previous = text;
            }
        }
        else
        {
            warnings.Add("Sin Ocr ni VisionModel: solo se transcribe el audio.");
        }

        var pages = BuildTimeline(frames, transcript, interval, options.VisionModel is not null ? PageTextSource.Vision : PageTextSource.Ocr);
        var header = new StringBuilder($"## Video: {source.FileName ?? "video"}\n\n");
        if (duration is { } total)
        {
            header.Append("Duración: ").Append(MediaHelpers.Timestamp(total)).Append("\n\n");
        }

        var markdown = header + string.Join("\n\n", pages.Select(p => p.Markdown));
        return new DocumentConversionResult
        {
            Markdown = markdown.Trim(),
            MediaType = source.MediaType,
            FileName = source.FileName,
            Pages = pages,
            Warnings = warnings,
        };
    }

    private static List<DocumentPage> BuildTimeline(List<(double Time, string Text)> frames, TranscriptionModelResult? transcript, double interval, PageTextSource frameSource)
    {
        var segments = transcript?.Segments ?? [];
        var pages = new List<DocumentPage>();
        if (frames.Count == 0 && segments.Count == 0)
        {
            if (transcript is { Text.Length: > 0 })
            {
                pages.Add(new DocumentPage(1, $"**Transcripción:** {transcript.Text.Trim()}", PageTextSource.Transcript));
            }

            return pages;
        }

        var windows = frames.Select(f => f.Time)
            .Concat(segments.Select(s => Math.Floor(s.StartSeconds / interval) * interval))
            .Distinct()
            .Order()
            .ToList();
        for (var i = 0; i < windows.Count; i++)
        {
            var start = windows[i];
            var end = i + 1 < windows.Count ? windows[i + 1] : double.MaxValue;
            var builder = new StringBuilder($"### [{MediaHelpers.Timestamp(start)}]\n");
            var frame = frames.FirstOrDefault(f => f.Time == start);
            if (frame.Text is not null)
            {
                builder.Append("**Escena:** ").Append(frame.Text.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
            }

            var speech = string.Join(' ', segments.Where(s => s.StartSeconds >= start && s.StartSeconds < end).Select(s => s.Text.Trim()));
            if (speech.Length > 0)
            {
                builder.Append("**Audio:** ").Append(speech).Append('\n');
            }

            pages.Add(new DocumentPage(i + 1, builder.ToString().Trim(), frame.Text is null ? PageTextSource.Transcript : frameSource)
            {
                Label = MediaHelpers.Timestamp(start),
            });
        }

        if (segments.Count == 0 && transcript is { Text.Length: > 0 })
        {
            pages.Add(new DocumentPage(pages.Count + 1, $"**Transcripción completa:** {transcript.Text.Trim()}", PageTextSource.Transcript));
        }

        return pages;
    }

    /// <summary>Input options for ffmpeg and ffprobe: only the <c>file</c> protocol and the formats in <see cref="AllowedFormats"/>.</summary>
    internal static readonly string[] SafeInputArguments = ["-protocol_whitelist", "file", "-format_whitelist", AllowedFormats];

    internal static async Task<double?> ProbeDurationAsync(IExternalProcessRunner runner, string ffmpeg, string input, CancellationToken cancellationToken)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty, Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase));
        if (!runner.Exists(ffprobe))
        {
            return null;
        }

        var result = await runner.RunAsync(ffprobe, ["-v", "error", .. SafeInputArguments, "-show_entries", "format=duration", "-of", "csv=p=0", input], cancellationToken: cancellationToken).ConfigureAwait(false);
        return double.TryParse(Encoding.UTF8.GetString(result.Output).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }
}

internal static class MediaHelpers
{
    public static Task<TranscriptionModelResult> TranscribeAsync(DocumentConversionContext context, byte[] audio, string mediaType, string? fileName, CancellationToken cancellationToken) =>
        Ai.TranscribeAsync(new TranscribeOptions
        {
            Model = context.Options.Transcription!,
            Audio = audio,
            MediaType = mediaType,
            Filename = fileName,
            Language = context.Options.Language,
            CancellationToken = cancellationToken,
        });

    public static string FormatTranscript(TranscriptionModelResult transcript) =>
        transcript.Segments.Count == 0
            ? transcript.Text.Trim()
            : string.Join("\n", transcript.Segments.Select(s => $"**[{Timestamp(s.StartSeconds)}]** {s.Text.Trim()}"));

    public static string Timestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }
}
