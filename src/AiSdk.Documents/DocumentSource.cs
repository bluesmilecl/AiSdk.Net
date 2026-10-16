using System.IO.Compression;
using System.Text;

namespace AiSdk.Documents;

/// <summary>Documento a convertir: bytes, tipo MIME y nombre de archivo.</summary>
public sealed record DocumentSource
{
    public DocumentSource(byte[] data, string? mediaType = null, string? fileName = null)
    {
        Data = data;
        FileName = fileName;
        MediaType = mediaType is null or "application/octet-stream" ? MediaTypes.Detect(data, fileName) : MediaTypes.Normalize(mediaType);
    }

    public byte[] Data { get; }

    /// <summary>Tipo MIME (indicado o detectado por extensión y firma del contenido).</summary>
    public string MediaType { get; }

    public string? FileName { get; }

    public static DocumentSource FromBytes(byte[] data, string? mediaType = null, string? fileName = null) => new(data, mediaType, fileName);

    public static DocumentSource FromText(string text, string mediaType = "text/plain", string? fileName = null) =>
        new(Encoding.UTF8.GetBytes(text), mediaType, fileName);

    public static async Task<DocumentSource> FromFileAsync(string path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        new(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), mediaType, Path.GetFileName(path));

    public static async Task<DocumentSource> FromStreamAsync(Stream stream, string? mediaType = null, string? fileName = null, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new(buffer.ToArray(), mediaType, fileName);
    }

    /// <summary>Texto decodificado (UTF-8 con detección de BOM).</summary>
    public string ReadText()
    {
        using var reader = new StreamReader(new MemoryStream(Data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>Tipos MIME conocidos y detección por extensión o firma.</summary>
public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string Html = "text/html";
    public const string Markdown = "text/markdown";
    public const string Csv = "text/csv";
    public const string Json = "application/json";
    public const string PlainText = "text/plain";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Pdf,
        [".docx"] = Docx,
        [".xlsx"] = Xlsx,
        [".xlsm"] = Xlsx,
        [".pptx"] = Pptx,
        [".html"] = Html,
        [".htm"] = Html,
        [".md"] = Markdown,
        [".markdown"] = Markdown,
        [".csv"] = Csv,
        [".tsv"] = "text/tab-separated-values",
        [".json"] = Json,
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".txt"] = PlainText,
        [".log"] = PlainText,
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".webm"] = "video/webm",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpg"] = "image/jpeg",
        ["audio/mp3"] = "audio/mpeg",
        ["audio/x-wav"] = "audio/wav",
        ["text/x-markdown"] = Markdown,
        ["application/x-pdf"] = Pdf,
    };

    public static string Normalize(string mediaType)
    {
        var value = mediaType.Split(';')[0].Trim().ToLowerInvariant();
        return Aliases.TryGetValue(value, out var alias) ? alias : value;
    }

    public static string? FromExtension(string? fileName) =>
        fileName is not null && ByExtension.TryGetValue(Path.GetExtension(fileName), out var type) ? type : null;

    /// <summary>Detecta el tipo por la firma del contenido y, si no hay coincidencia, por la extensión.</summary>
    public static string Detect(ReadOnlySpan<byte> data, string? fileName = null)
    {
        var sniffed = Sniff(data);
        var byName = FromExtension(fileName);

        // Las firmas genéricas (ZIP, RIFF, ftyp) se refinan con la extensión.
        return sniffed switch
        {
            "application/zip" => byName is Docx or Xlsx or Pptx ? byName : SniffOfficeZip(data.ToArray()) ?? byName ?? sniffed,
            "video/mp4" when byName is "audio/mp4" => byName,
            "video/webm" when byName?.StartsWith("audio/", StringComparison.Ordinal) == true => byName,
            null => byName ?? (LooksLikeText(data) ? PlainText : "application/octet-stream"),
            _ => sniffed,
        };
    }

    public static bool IsImage(string mediaType) => mediaType.StartsWith("image/", StringComparison.Ordinal);

    public static bool IsAudio(string mediaType) => mediaType.StartsWith("audio/", StringComparison.Ordinal);

    public static bool IsVideo(string mediaType) => mediaType.StartsWith("video/", StringComparison.Ordinal);

    /// <summary>Extensión recomendada para un tipo MIME (p. ej. para archivos temporales).</summary>
    public static string ExtensionFor(string mediaType) =>
        ByExtension.FirstOrDefault(kv => kv.Value == mediaType).Key ?? ".bin";

    private static string? Sniff(ReadOnlySpan<byte> d)
    {
        static bool At(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> sig) =>
            data.Length >= offset + sig.Length && data.Slice(offset, sig.Length).SequenceEqual(sig);

        if (At(d, 0, "%PDF"u8)) return Pdf;
        if (At(d, 0, [0x89, 0x50, 0x4E, 0x47])) return "image/png";
        if (At(d, 0, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (At(d, 0, "GIF8"u8)) return "image/gif";
        if (At(d, 0, "BM"u8) && d.Length > 14) return "image/bmp";
        if (At(d, 0, [0x49, 0x49, 0x2A, 0x00]) || At(d, 0, [0x4D, 0x4D, 0x00, 0x2A])) return "image/tiff";
        if (At(d, 0, "RIFF"u8) && At(d, 8, "WEBP"u8)) return "image/webp";
        if (At(d, 0, "RIFF"u8) && At(d, 8, "WAVE"u8)) return "audio/wav";
        if (At(d, 0, "RIFF"u8) && At(d, 8, "AVI "u8)) return "video/x-msvideo";
        if (At(d, 0, "ID3"u8) || (d.Length > 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0)) return "audio/mpeg";
        if (At(d, 0, "OggS"u8)) return "audio/ogg";
        if (At(d, 0, "fLaC"u8)) return "audio/flac";
        if (At(d, 0, [0x1A, 0x45, 0xDF, 0xA3])) return "video/webm";
        if (At(d, 4, "ftyp"u8))
        {
            return At(d, 8, "M4A"u8) ? "audio/mp4" : At(d, 8, "qt"u8) ? "video/quicktime" : "video/mp4";
        }

        if (At(d, 0, [0x50, 0x4B, 0x03, 0x04])) return "application/zip";
        return null;
    }

    private static string? SniffOfficeZip(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("word/", StringComparison.Ordinal)) return Docx;
                if (entry.FullName.StartsWith("xl/", StringComparison.Ordinal)) return Xlsx;
                if (entry.FullName.StartsWith("ppt/", StringComparison.Ordinal)) return Pptx;
            }
        }
        catch (InvalidDataException)
        {
        }

        return null;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(data.Length, 4096)];
        if (sample.IsEmpty)
        {
            return true;
        }

        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                control++;
            }
        }

        return control < sample.Length / 50 + 1;
    }
}
