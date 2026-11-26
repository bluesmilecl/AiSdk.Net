using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AiSdk.Resources;

namespace AiSdk.UI;

/// <summary>
/// Issues and verifies the <c>approvalId</c> values of the AI SDK UI tool approval protocol: the
/// <c>tool-approval-request</c> chunk that shows the part in the <c>approval-requested</c> state, and the response
/// (<c>addToolApprovalResponse</c>, <c>approval-responded</c> state) that the browser sends back in the history.
/// </summary>
/// <remarks>
/// <para>
/// <c>useChat</c> resends the whole conversation on every request, and the browser can change it: it could "approve" a
/// call the model never made or change the input of a real call before approving it. That is why the approvalId is not
/// a random id but an opaque, signed token that binds the approval to the exact call the server issued:
/// its id, the tool and the input (canonical JSON, which does not change on the round trip through the browser), within
/// a scope (user and chat) and with an expiration. <see cref="UIMessageConverter.ToModelInput"/> only accepts a
/// response if its approvalId verifies against the call the client resends: if the input, the id or the
/// tool changed, or the token belongs to another user or another chat, or it expired, the call is not executed.
/// </para>
/// <para>
/// It is stateless (like the AI SDK's optional signature): within its lifetime, the same user could resend an
/// approval already used. If the approver is not the person chatting, or a call must never be repeated, store the
/// conversation on the server (<see cref="AgentSession.SendApprovalsAsync"/>) or use a workflow with an approval
/// event.
/// </para>
/// <para>
/// In ASP.NET Core use <c>ToolApprovalProtection.ForChat</c> (Data Protection, no secrets to configure). Outside
/// ASP.NET Core, use <see cref="CreateHmac"/>. For another key store, inherit and implement <see cref="Protect"/> and
/// <see cref="Verify(string, ReadOnlySpan{byte})"/>.
/// </para>
/// </remarks>
public abstract class ToolApprovalProtector
{
    /// <summary>Default lifetime of an approvalId (1 hour).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    /// <summary>Maximum accepted length of an approvalId (anything longer is not verified).</summary>
    private const int MaxApprovalIdLength = 2048;

    /// <summary>Creates the approvalId of a call that is left pending approval.</summary>
    public string CreateApprovalId(ToolCallPart call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Protect(Digest(call));
    }

    /// <summary>
    /// True if <paramref name="approvalId"/> was issued by this protector (same key and scope), has not expired and
    /// matches <paramref name="call"/> exactly: same call id, tool and input.
    /// </summary>
    public bool VerifyApprovalId(string? approvalId, ToolCallPart call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return approvalId is { Length: > 0 and <= MaxApprovalIdLength } && Verify(approvalId, Digest(call));
    }

    /// <summary>Seals the digest (SHA-256) of the call into an opaque approvalId, suitable for a URL or JSON.</summary>
    protected abstract string Protect(ReadOnlySpan<byte> digest);

    /// <summary>True if <paramref name="approvalId"/> is valid and seals exactly <paramref name="digest"/>.</summary>
    protected abstract bool Verify(string approvalId, ReadOnlySpan<byte> digest);

    /// <summary>
    /// Protector with HMAC-SHA256 and your own key (at least 32 random bytes, the same on all server
    /// instances; store it as a secret). <paramref name="scope"/> restricts the ids to a scope, e.g. user and chat
    /// (<see cref="ResumableUIMessageStream.ScopedChatId"/>): an id issued in another scope does not verify.
    /// </summary>
    public static ToolApprovalProtector CreateHmac(ReadOnlySpan<byte> key, string scope, TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException(Strings.Approval_KeyTooShort, nameof(key));
        }

        ArgumentException.ThrowIfNullOrEmpty(scope);
        var life = lifetime ?? DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero, nameof(lifetime));
        return new HmacProtector(key.ToArray(), scope, life, timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// SHA-256 of the call: id, tool and input in canonical JSON (sorted keys, numbers as double), which is
    /// the same before and after the browser deserializes and reserializes it.
    /// </summary>
    private static byte[] Digest(ToolCallPart call)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("aisdk.tool-approval/v1");
            writer.WriteStringValue(call.ToolCallId);
            writer.WriteStringValue(call.ToolName);
            WriteCanonical(writer, call.Input);
            writer.WriteEndArray();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                // Como JSON.parse: con claves repetidas gana la última. Orden ordinal (unidades UTF-16), como en JavaScript.
                var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    properties[property.Name] = property.Value;
                }

                writer.WriteStartObject();
                foreach (var (name, item) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    WriteCanonical(writer, item);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                string? text;
                try
                {
                    text = value.GetString();
                }
                catch (InvalidOperationException)
                {
                    text = null; // escapes inválidos (p. ej. un surrogate suelto)
                }

                if (text is not null && !HasLoneSurrogate(text))
                {
                    writer.WriteStringValue(text);
                }
                else
                {
                    // El texto crudo de un string inválido nunca coincide con el de un string válido escrito arriba.
                    writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                }

                break;
            case JsonValueKind.Number:
                // JavaScript guarda los números como double: 1.0, 1e0 y 1 son el mismo valor tras el viaje por el navegador.
                if (value.TryGetDouble(out var number) && double.IsFinite(number))
                {
                    writer.WriteNumberValue(number == 0 ? 0d : number);
                }
                else
                {
                    writer.WriteRawValue(value.GetRawText());
                }

                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>approvalId <c>v1.{expiry (Unix seconds)}.{HMAC-SHA256}</c> over the scope, the expiration and the digest.</summary>
    private sealed class HmacProtector(byte[] key, string scope, TimeSpan lifetime, TimeProvider time) : ToolApprovalProtector
    {
        protected override string Protect(ReadOnlySpan<byte> digest)
        {
            var expires = (time.GetUtcNow() + lifetime).ToUnixTimeSeconds();
            return string.Create(CultureInfo.InvariantCulture, $"v1.{expires}.{Base64Url.EncodeToString(Mac(expires, digest))}");
        }

        protected override bool Verify(string approvalId, ReadOnlySpan<byte> digest)
        {
            var parts = approvalId.Split('.');
            if (parts.Length != 3
                || parts[0] != "v1"
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
                || time.GetUtcNow().ToUnixTimeSeconds() > expires)
            {
                return false;
            }

            Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
            return Base64Url.IsValid(parts[2], out var length)
                   && length == mac.Length
                   && Base64Url.TryDecodeFromChars(parts[2], mac, out var written)
                   && written == mac.Length
                   && CryptographicOperations.FixedTimeEquals(mac, Mac(expires, digest));
        }

        private byte[] Mac(long expires, ReadOnlySpan<byte> digest)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStringValue("aisdk.tool-approval.hmac/v1");
                writer.WriteStringValue(scope);
                writer.WriteNumberValue(expires);
                writer.WriteBase64StringValue(digest);
                writer.WriteEndArray();
            }

            return HMACSHA256.HashData(key, buffer.WrittenSpan);
        }
    }
}
