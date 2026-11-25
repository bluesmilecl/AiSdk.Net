using System.Buffers.Binary;

namespace AiSdk.Documents;

/// <summary>
/// Ancho y alto de una imagen leídos de su cabecera, sin decodificarla (PNG, JPEG, GIF, BMP y WebP). Sirve para rechazar
/// "bombas" de descompresión: unos pocos KB que, decodificados, ocuparían gigabytes.
/// </summary>
internal static class ImageHeader
{
    public static bool TryGetSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        (width, height) = (0, 0);
        try
        {
            if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                // IHDR: ancho y alto en big endian tras la firma y el encabezado del bloque.
                (width, height) = (BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]));
                return width > 0 && height > 0;
            }

            if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            {
                (width, height) = (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
                return true;
            }

            if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
            {
                (width, height) = (Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[18..])), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..])));
                return width > 0 && height > 0;
            }

            if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            {
                return TryWebP(data, out width, out height);
            }

            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            {
                return TryJpeg(data, out width, out height);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Cabecera truncada.
        }

        (width, height) = (0, 0);
        return false;
    }

    private static bool TryWebP(ReadOnlySpan<byte> data, out int width, out int height)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + (data[24] | data[25] << 8 | data[26] << 16);
            height = 1 + (data[27] | data[28] << 8 | data[29] << 16);
            return true;
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        if (chunk.SequenceEqual("VP8 "u8))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF;
            return width > 0 && height > 0;
        }

        (width, height) = (0, 0);
        return false;
    }

    private static bool TryJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        // Recorre los segmentos hasta el primer SOFn (C0-CF salvo DHT C4, JPG C8 y DAC CC).
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];
            if (marker is 0xFF or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += marker == 0xFF ? 1 : 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..]);
                return width > 0 && height > 0;
            }

            if (length < 2)
            {
                break;
            }

            i += 2 + length;
        }

        (width, height) = (0, 0);
        return false;
    }
}
