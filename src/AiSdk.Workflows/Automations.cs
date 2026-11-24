using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;

namespace AiSdk.Workflows;

/// <summary>Disparador de una automatización.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CronTrigger), "cron")]
[JsonDerivedType(typeof(IntervalTrigger), "interval")]
[JsonDerivedType(typeof(OnceTrigger), "once")]
[JsonDerivedType(typeof(EventTrigger), "event")]
[JsonDerivedType(typeof(WebhookTrigger), "webhook")]
public abstract record AutomationTrigger
{
    /// <summary>Próxima ejecución programada después de <paramref name="after"/> (null si no es temporal o ya no aplica).</summary>
    public virtual DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) => null;

    /// <summary>Descripción legible (para listas y para el modelo).</summary>
    public abstract string Describe();

    /// <summary>Comprueba la configuración; lanza <see cref="ArgumentException"/> si no es válida.</summary>
    public virtual void Validate()
    {
    }
}

/// <summary>
/// Expresión cron de 5 campos (minuto hora día mes día-semana) o 6 con segundos, en la zona horaria
/// indicada (id IANA o de Windows, p. ej. <c>"America/Santiago"</c>). Ejemplo: <c>"0 9 * * 1-5"</c>.
/// </summary>
public sealed record CronTrigger(string Expression, string? TimeZone = null) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) =>
        Parse(Expression).GetNextOccurrence(after, ResolveZone());

    public override string Describe() => $"cron \"{Expression}\"{(TimeZone is null ? " (UTC)" : $" ({TimeZone})")}";

    /// <summary>Valida la expresión y que la zona horaria exista en este sistema.</summary>
    public override void Validate()
    {
        try
        {
            Parse(Expression);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"Expresión cron inválida '{Expression}': {ex.Message}", nameof(Expression), ex);
        }

        try
        {
            ResolveZone();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"La zona horaria '{TimeZone}' no existe en este sistema.", nameof(TimeZone), ex);
        }
    }

    private TimeZoneInfo ResolveZone() => TimeZone is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>Valida y parsea la expresión (5 o 6 campos).</summary>
    public static CronExpression Parse(string expression) =>
        CronExpression.Parse(expression, expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
}

/// <summary>Cada cierto intervalo (mínimo un segundo).</summary>
public sealed record IntervalTrigger(TimeSpan Every) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) =>
        lastRun is { } last && last + Every > after ? last + Every : after + Every;

    public override string Describe() => $"cada {Every}";

    public override void Validate()
    {
        if (Every < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("El intervalo debe ser de al menos un segundo.", nameof(Every));
        }
    }
}

/// <summary>Una sola vez, en la fecha indicada.</summary>
public sealed record OnceTrigger(DateTimeOffset At) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) => lastRun is null ? At : null;

    public override string Describe() => $"una vez el {At:yyyy-MM-dd HH:mm} UTC";
}

/// <summary>
/// Cuando ocurre un evento (<see cref="WorkflowEngine.SendEventAsync"/>). <see cref="Match"/> filtra por
/// propiedades del contenido (igualdad de texto), p. ej. <c>{ "estado": "pagado" }</c>.
/// </summary>
public sealed record EventTrigger(string EventName, IReadOnlyDictionary<string, string>? Match = null) : AutomationTrigger
{
    public bool Matches(string eventName, JsonElement payload) =>
        eventName == EventName
        && (Match is null || Match.All(kv =>
            payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(kv.Key, out var value)
            && (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()) == kv.Value));

    public override string Describe() => $"evento \"{EventName}\"";
}

/// <summary>Cómo autentica un <see cref="WebhookTrigger"/> las peticiones entrantes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebhookVerification>))]
public enum WebhookVerification
{
    /// <summary>
    /// (Por defecto) Firma HMAC-SHA256 de <c>"{timestamp}.{cuerpo}"</c> en <c>X-Signature-256: sha256=...</c>, con la marca de
    /// tiempo (segundos Unix) en <c>X-Webhook-Timestamp</c> dentro de <see cref="WorkflowEngineOptions.WebhookTolerance"/>.
    /// Una petición capturada no se puede repetir: la misma firma devuelve la ejecución ya creada y, pasada la
    /// tolerancia, se rechaza. <c>X-Webhook-Id</c> (opcional) deduplica los reintentos del emisor.
    /// </summary>
    TimestampedSignature,

    /// <summary>
    /// Firma HMAC-SHA256 solo del cuerpo (<c>X-Signature-256</c> o <c>X-Hub-Signature-256</c>, compatible con GitHub).
    /// Sin marca de tiempo, la protección contra repeticiones depende del id de entrega (<c>X-Webhook-Id</c> o
    /// <c>X-GitHub-Delivery</c>), que no va firmado: úsela solo con emisores que no admiten la firma con marca de tiempo.
    /// </summary>
    BodySignature,

    /// <summary>
    /// Secreto compartido en la cabecera <c>X-Webhook-Secret</c>. Quien capture una petición puede enviar otras con
    /// cualquier contenido: úsela solo si el emisor no sabe firmar.
    /// </summary>
    SharedSecret,
}

/// <summary>
/// Llamada HTTP a <c>POST {prefijo}/hooks/{id}</c>. Requiere <see cref="Secret"/> (al menos 16 caracteres; genere uno con
/// <see cref="WebhookSignature.CreateSecret"/>) y por defecto una firma con marca de tiempo
/// (<see cref="WebhookVerification.TimestampedSignature"/>). Para aceptar webhooks sin autenticar hay que indicar
/// <see cref="AllowUnauthenticated"/> = true de forma explícita.
/// </summary>
public sealed record WebhookTrigger(string? Secret = null) : AutomationTrigger
{
    /// <summary>Longitud mínima del secreto.</summary>
    public const int MinimumSecretLength = 16;

    /// <summary>Cómo se autentican las peticiones. Por defecto, <see cref="WebhookVerification.TimestampedSignature"/>.</summary>
    public WebhookVerification Verification { get; init; } = WebhookVerification.TimestampedSignature;

    /// <summary>
    /// Acepta peticiones sin secreto: cualquiera que conozca la URL dispara el workflow con el contenido que quiera.
    /// Solo para pruebas o redes de confianza; use entonces un id de automatización aleatorio (<see cref="Automation.Create{TInput}"/>).
    /// </summary>
    public bool AllowUnauthenticated { get; init; }

    public override string Describe() => Secret is null ? "webhook (sin autenticar)" : $"webhook ({Verification})";

    public override void Validate()
    {
        if (string.IsNullOrEmpty(Secret))
        {
            if (!AllowUnauthenticated)
            {
                throw new ArgumentException(
                    "Un WebhookTrigger necesita un secreto (Secret). Para aceptar webhooks sin autenticar indique AllowUnauthenticated = true de forma explícita.",
                    nameof(Secret));
            }

            return;
        }

        if (Secret.Length < MinimumSecretLength)
        {
            throw new ArgumentException($"El secreto del webhook debe tener al menos {MinimumSecretLength} caracteres (genere uno con WebhookSignature.CreateSecret()).", nameof(Secret));
        }
    }
}

/// <summary>Cabeceras y firmas de los webhooks de <see cref="WebhookTrigger"/>.</summary>
public static class WebhookSignature
{
    /// <summary>Firma: <c>sha256={hex}</c>.</summary>
    public const string SignatureHeader = "X-Signature-256";

    /// <summary>Firma con el nombre que usa GitHub (solo cuerpo).</summary>
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";

    /// <summary>Marca de tiempo de la firma, en segundos Unix.</summary>
    public const string TimestampHeader = "X-Webhook-Timestamp";

    /// <summary>Id único de la entrega (deduplica reintentos).</summary>
    public const string DeliveryIdHeader = "X-Webhook-Id";

    /// <summary>Id de entrega con el nombre que usa GitHub.</summary>
    public const string GitHubDeliveryHeader = "X-GitHub-Delivery";

    /// <summary>Secreto compartido (<see cref="WebhookVerification.SharedSecret"/>).</summary>
    public const string SecretHeader = "X-Webhook-Secret";

    /// <summary>Genera un secreto aleatorio de 256 bits (64 caracteres hexadecimales).</summary>
    public static string CreateSecret() => RandomNumberGenerator.GetHexString(64, lowercase: true);

    /// <summary>
    /// Firma de <see cref="WebhookVerification.TimestampedSignature"/>: <c>sha256=</c> + HMAC-SHA256 en hexadecimal de
    /// <c>"{timestamp}.{cuerpo}"</c>. Úsela en el emisor (o en pruebas) junto con la cabecera <see cref="TimestampHeader"/>.
    /// </summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
    }

    /// <summary>Firma de <see cref="WebhookVerification.BodySignature"/> (solo el cuerpo, como GitHub).</summary>
    public static string SignBody(string secret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Compara una firma recibida con la esperada en tiempo constante.</summary>
    public static bool Matches(string expected, string? received) =>
        received is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received.Trim().ToLowerInvariant()), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Petición de webhook entrante: cuerpo y cabeceras relevantes.</summary>
/// <param name="Body">Cuerpo tal cual llegó (la firma se calcula sobre estos bytes).</param>
public sealed record WebhookRequest(ReadOnlyMemory<byte> Body)
{
    /// <summary>Cabecera <see cref="WebhookSignature.SignatureHeader"/> (o la de GitHub).</summary>
    public string? Signature { get; init; }

    /// <summary>Cabecera <see cref="WebhookSignature.TimestampHeader"/>.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Cabecera <see cref="WebhookSignature.DeliveryIdHeader"/> (o la de GitHub).</summary>
    public string? DeliveryId { get; init; }

    /// <summary>Cabecera <see cref="WebhookSignature.SecretHeader"/>.</summary>
    public string? Secret { get; init; }

    /// <summary>Construye la petición leyendo las cabeceras documentadas con <paramref name="header"/> (nombre → valor o null).</summary>
    public static WebhookRequest FromHeaders(ReadOnlyMemory<byte> body, Func<string, string?> header) => new(body)
    {
        Signature = NotEmpty(header(WebhookSignature.SignatureHeader)) ?? NotEmpty(header(WebhookSignature.GitHubSignatureHeader)),
        Timestamp = NotEmpty(header(WebhookSignature.TimestampHeader)),
        DeliveryId = NotEmpty(header(WebhookSignature.DeliveryIdHeader)) ?? NotEmpty(header(WebhookSignature.GitHubDeliveryHeader)),
        Secret = NotEmpty(header(WebhookSignature.SecretHeader)),
    };

    private static string? NotEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>Automatización: cuando ocurre el disparador, se ejecuta el workflow con la entrada indicada.</summary>
public sealed record Automation
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required AutomationTrigger Trigger { get; init; }

    /// <summary>Nombre del workflow registrado a ejecutar.</summary>
    public required string Workflow { get; init; }

    /// <summary>Entrada fija. Si es null, se usa el contenido del evento o webhook.</summary>
    public JsonElement? Input { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? NextRunAt { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public string? LastRunId { get; init; }

    /// <summary>Usuario o agente que la creó (para limitar quién puede verla o cancelarla).</summary>
    public string? Owner { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Fallos seguidos al dispararla (workflow no registrado, zona horaria inexistente, almacén caído...). Cada fallo
    /// aplaza el siguiente intento; al llegar a <see cref="WorkflowEngineOptions.MaxAutomationFailures"/> se desactiva.
    /// </summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Mensaje del último fallo (null si el último disparo funcionó).</summary>
    public string? LastError { get; init; }

    /// <summary>Versión para concurrencia optimista (la gestiona el almacén; 0 si aún no se guardó).</summary>
    public long Version { get; init; }

    /// <summary>
    /// Crea una automatización con entrada tipada. Si no se indica <paramref name="id"/>, se genera uno con 128 bits
    /// aleatorios (la URL de un webhook no se puede adivinar).
    /// </summary>
    public static Automation Create<TInput>(string name, AutomationTrigger trigger, string workflow, TInput input, string? id = null) => new()
    {
        Id = id ?? Artifacts.ArtifactTools.Slug(name) + "-" + RandomNumberGenerator.GetHexString(32, lowercase: true),
        Name = name,
        Trigger = trigger,
        Workflow = workflow,
        Input = JsonSerializer.SerializeToElement(input, JsonSchema.DefaultSerializerOptions),
    };
}
