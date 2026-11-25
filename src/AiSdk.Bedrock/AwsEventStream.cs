using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AiSdk.Bedrock;

/// <summary>Message in the <c>application/vnd.amazon.eventstream</c> binary format.</summary>
public sealed record AwsEventStreamMessage(IReadOnlyDictionary<string, string> Headers, byte[] Payload)
{
    public string? EventType => Headers.GetValueOrDefault(":event-type");

    public string? MessageType => Headers.GetValueOrDefault(":message-type");

    public string? ExceptionType => Headers.GetValueOrDefault(":exception-type");

    public string PayloadText => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// Reader for the AWS <c>event-stream</c> format: each message has a prelude (total length,
/// headers length, CRC), typed headers, a payload and a trailing CRC32.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Nombre del formato binario event-stream de AWS; es una clase estática de utilidades, no un Stream.")]
public static class AwsEventStream
{
    /// <summary>Default maximum size of a message (AWS limits event-stream messages to 16 MB).</summary>
    public const int DefaultMaxMessageBytes = 16 * 1024 * 1024 + 1024;

    /// <summary>Reads the messages from the stream with the default maximum size (<see cref="DefaultMaxMessageBytes"/>).</summary>
    public static IAsyncEnumerable<AwsEventStreamMessage> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync(stream, DefaultMaxMessageBytes, cancellationToken);

    /// <summary>
    /// Reads the messages from the stream. A message whose prelude declares more than <paramref name="maxMessageBytes"/> is
    /// rejected before allocating memory (the server cannot force allocations of up to 2 GB).
    /// </summary>
    public static async IAsyncEnumerable<AwsEventStreamMessage> ReadAsync(Stream stream, int maxMessageBytes, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prelude = new byte[12];
        while (true)
        {
            if (!await ReadExactlyOrEndAsync(stream, prelude, cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }

            var totalLength = BinaryPrimitives.ReadInt32BigEndian(prelude.AsSpan(0, 4));
            var headersLength = BinaryPrimitives.ReadInt32BigEndian(prelude.AsSpan(4, 4));
            var preludeCrc = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(8, 4));
            if (Crc32(prelude.AsSpan(0, 8)) != preludeCrc)
            {
                throw new AiSdkException(Resources.Strings.EventStream_InvalidPreludeCrc);
            }

            if (totalLength < 16 || headersLength < 0 || headersLength > totalLength - 16)
            {
                throw new AiSdkException(Resources.Strings.EventStream_InvalidLengths);
            }

            if (totalLength > maxMessageBytes)
            {
                throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.EventStream_MessageTooLarge, totalLength, maxMessageBytes));
            }

            var message = new byte[totalLength];
            prelude.CopyTo(message, 0);
            await stream.ReadExactlyAsync(message.AsMemory(12), cancellationToken).ConfigureAwait(false);

            var messageCrc = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(totalLength - 4));
            if (Crc32(message.AsSpan(0, totalLength - 4)) != messageCrc)
            {
                throw new AiSdkException(Resources.Strings.EventStream_InvalidMessageCrc);
            }

            var headers = ParseHeaders(message.AsSpan(12, headersLength));
            var payload = message.AsSpan(12 + headersLength, totalLength - headersLength - 16).ToArray();
            yield return new AwsEventStreamMessage(headers, payload);
        }
    }

    /// <summary>Encodes a message (useful for tests). String headers only.</summary>
    public static byte[] Encode(IReadOnlyDictionary<string, string> headers, byte[] payload)
    {
        using var headerBytes = new MemoryStream();
        Span<byte> len = stackalloc byte[2];
        foreach (var (name, value) in headers)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            headerBytes.WriteByte((byte)nameBytes.Length);
            headerBytes.Write(nameBytes);
            headerBytes.WriteByte(7);
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)valueBytes.Length);
            headerBytes.Write(len);
            headerBytes.Write(valueBytes);
        }

        var h = headerBytes.ToArray();
        var total = 16 + h.Length + payload.Length;
        var message = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0, 4), total);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4, 4), h.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8, 4), Crc32(message.AsSpan(0, 8)));
        h.CopyTo(message, 12);
        payload.CopyTo(message, 12 + h.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(total - 4), Crc32(message.AsSpan(0, total - 4)));
        return message;
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> data)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < data.Length)
        {
            var nameLength = data[i++];
            var name = Encoding.UTF8.GetString(data.Slice(i, nameLength));
            i += nameLength;
            var type = data[i++];
            string value;
            switch (type)
            {
                case 0:
                    value = "true";
                    break;
                case 1:
                    value = "false";
                    break;
                case 2:
                    value = ((sbyte)data[i]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    i += 1;
                    break;
                case 3:
                    value = BinaryPrimitives.ReadInt16BigEndian(data.Slice(i, 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    i += 2;
                    break;
                case 4:
                    value = BinaryPrimitives.ReadInt32BigEndian(data.Slice(i, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    i += 4;
                    break;
                case 5:
                case 8:
                    value = BinaryPrimitives.ReadInt64BigEndian(data.Slice(i, 8)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    i += 8;
                    break;
                case 6:
                case 7:
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
                    i += 2;
                    var bytes = data.Slice(i, length);
                    value = type == 7 ? Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes);
                    i += length;
                    break;
                }

                case 9:
                    value = new Guid(data.Slice(i, 16), bigEndian: true).ToString();
                    i += 16;
                    break;
                default:
                    throw new AiSdkException(string.Format(CultureInfo.CurrentCulture, Resources.Strings.EventStream_UnknownHeaderType, type));
            }

            headers[name] = value;
        }

        return headers;
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new AiSdkException(Resources.Strings.EventStream_Truncated);
            }

            read += n;
        }

        return true;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>CRC-32 (IEEE 802.3).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
