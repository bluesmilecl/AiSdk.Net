using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiSdk.UI;

/// <summary>
/// Emite y verifica los <c>approvalId</c> del protocolo de aprobación de herramientas de AI SDK UI: el chunk
/// <c>tool-approval-request</c> que muestra la parte en el estado <c>approval-requested</c> y la respuesta
/// (<c>addToolApprovalResponse</c>, estado <c>approval-responded</c>) que el navegador devuelve en el historial.
/// </summary>
/// <remarks>
/// <para>
/// <c>useChat</c> reenvía en cada petición toda la conversación, y el navegador puede cambiarla: podría "aprobar" una
/// llamada que el modelo nunca hizo o cambiar la entrada de una llamada real antes de aprobarla. Por eso el approvalId no
/// es un id aleatorio sino un token opaco y firmado que vincula la aprobación a la llamada exacta que emitió el servidor:
/// su id, la herramienta y la entrada (JSON canónico, que no cambia en el viaje de ida y vuelta por el navegador), dentro
/// de un ámbito (usuario y chat) y con caducidad. <see cref="UIMessageConverter.ToModelInput"/> solo acepta una
/// respuesta si su approvalId verifica contra la llamada que reenvía el cliente: si cambió la entrada, el id o la
/// herramienta, o el token es de otro usuario, de otro chat o expiró, la llamada no se ejecuta.
/// </para>
/// <para>
/// No guarda estado (como la firma opcional del AI SDK): dentro de su vigencia, el mismo usuario podría reenviar una
/// aprobación que ya usó. Si quien aprueba no es quien conversa, o una llamada no debe repetirse nunca, guarde la
/// conversación en el servidor (<see cref="AgentSession.SendApprovalsAsync"/>) o use un workflow con un evento de
/// aprobación.
/// </para>
/// <para>
/// En ASP.NET Core use <c>ToolApprovalProtection.ForChat</c> (Data Protection, sin secretos que configurar). Fuera de
/// ASP.NET Core, <see cref="CreateHmac"/>. Para otro almacén de claves, herede e implemente <see cref="Protect"/> y
/// <see cref="Verify(string, ReadOnlySpan{byte})"/>.
/// </para>
/// </remarks>
public abstract class ToolApprovalProtector
{
    /// <summary>Vigencia por defecto de un approvalId (1 hora).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    /// <summary>Largo máximo aceptado de un approvalId (lo que excede no se intenta verificar).</summary>
    private const int MaxApprovalIdLength = 2048;

    /// <summary>Crea el approvalId de una llamada que queda pendiente de aprobación.</summary>
    public string CreateApprovalId(ToolCallPart call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Protect(Digest(call));
    }

    /// <summary>
    /// True si <paramref name="approvalId"/> lo emitió este protector (misma clave y ámbito), no expiró y corresponde
    /// exactamente a <paramref name="call"/>: mismo id de llamada, herramienta y entrada.
    /// </summary>
    public bool VerifyApprovalId(string? approvalId, ToolCallPart call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return approvalId is { Length: > 0 and <= MaxApprovalIdLength } && Verify(approvalId, Digest(call));
    }

    /// <summary>Sella el resumen (SHA-256) de la llamada en un approvalId opaco, apto para una URL o JSON.</summary>
    protected abstract string Protect(ReadOnlySpan<byte> digest);

    /// <summary>True si <paramref name="approvalId"/> es válido y sella exactamente <paramref name="digest"/>.</summary>
    protected abstract bool Verify(string approvalId, ReadOnlySpan<byte> digest);

    /// <summary>
    /// Protector con HMAC-SHA256 y una clave propia (al menos 32 bytes aleatorios, la misma en todas las instancias del
    /// servidor; guárdela como un secreto). <paramref name="scope"/> limita los ids a un ámbito, p. ej. usuario y chat
    /// (<see cref="ResumableUIMessageStream.ScopedChatId"/>): un id emitido en otro ámbito no verifica.
    /// </summary>
    public static ToolApprovalProtector CreateHmac(ReadOnlySpan<byte> key, string scope, TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("La clave debe tener al menos 32 bytes (256 bits) aleatorios.", nameof(key));
        }

        ArgumentException.ThrowIfNullOrEmpty(scope);
        var life = lifetime ?? DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero, nameof(lifetime));
        return new HmacProtector(key.ToArray(), scope, life, timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// SHA-256 de la llamada: id, herramienta y entrada en JSON canónico (claves ordenadas, números como double), que es
    /// igual antes y después de que el navegador la deserialice y la vuelva a serializar.
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

    /// <summary>approvalId <c>v1.{expira (segundos Unix)}.{HMAC-SHA256}</c> sobre el ámbito, la caducidad y el resumen.</summary>
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
