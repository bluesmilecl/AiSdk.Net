using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;

namespace AiSdk.Workflows;

/// <summary>Trigger of an automation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CronTrigger), "cron")]
[JsonDerivedType(typeof(IntervalTrigger), "interval")]
[JsonDerivedType(typeof(OnceTrigger), "once")]
[JsonDerivedType(typeof(EventTrigger), "event")]
[JsonDerivedType(typeof(WebhookTrigger), "webhook")]
public abstract record AutomationTrigger
{
    /// <summary>Next scheduled run after <paramref name="after"/> (null if the trigger is not time-based or no longer applies).</summary>
    public virtual DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) => null;

    /// <summary>Human-readable description (for listings and for the model).</summary>
    public abstract string Describe();

    /// <summary>Checks the configuration; throws <see cref="ArgumentException"/> if it is not valid.</summary>
    public virtual void Validate()
    {
    }
}

/// <summary>
/// 5-field cron expression (minute hour day month day-of-week), or 6 fields with seconds, in the given time zone
/// (IANA or Windows id, e.g. <c>"America/Santiago"</c>). Example: <c>"0 9 * * 1-5"</c>.
/// </summary>
public sealed record CronTrigger(string Expression, string? TimeZone = null) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) =>
        Parse(Expression).GetNextOccurrence(after, ResolveZone());

    public override string Describe() => $"cron \"{Expression}\"{(TimeZone is null ? " (UTC)" : $" ({TimeZone})")}";

    /// <summary>Validates the expression and that the time zone exists on this system.</summary>
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

    /// <summary>Validates and parses the expression (5 or 6 fields).</summary>
    public static CronExpression Parse(string expression) =>
        CronExpression.Parse(expression, expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
}

/// <summary>At a fixed interval (minimum one second).</summary>
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

/// <summary>Only once, at the given date.</summary>
public sealed record OnceTrigger(DateTimeOffset At) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) => lastRun is null ? At : null;

    public override string Describe() => $"una vez el {At:yyyy-MM-dd HH:mm} UTC";
}

/// <summary>
/// When an event occurs (<see cref="WorkflowEngine.SendEventAsync"/>). <see cref="Match"/> filters by properties of the
/// payload (text equality), e.g. <c>{ "status": "paid" }</c>.
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

/// <summary>How a <see cref="WebhookTrigger"/> authenticates incoming requests.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebhookVerification>))]
public enum WebhookVerification
{
    /// <summary>
    /// (Default) HMAC-SHA256 signature of <c>"{timestamp}.{body}"</c> in <c>X-Signature-256: sha256=...</c>, with the
    /// timestamp (Unix seconds) in <c>X-Webhook-Timestamp</c> within <see cref="WorkflowEngineOptions.WebhookTolerance"/>.
    /// A captured request cannot be replayed: the same signature returns the run already created and, once the tolerance
    /// has passed, it is rejected. <c>X-Webhook-Id</c> (optional) deduplicates the sender's retries.
    /// </summary>
    TimestampedSignature,

    /// <summary>
    /// HMAC-SHA256 signature of the body only (<c>X-Signature-256</c> or <c>X-Hub-Signature-256</c>, GitHub-compatible).
    /// Without a timestamp, replay protection depends on the delivery id (<c>X-Webhook-Id</c> or
    /// <c>X-GitHub-Delivery</c>), which is not signed: use it only with senders that do not support the timestamped signature.
    /// </summary>
    BodySignature,

    /// <summary>
    /// Shared secret in the <c>X-Webhook-Secret</c> header. Whoever captures a request can send others with any content:
    /// use it only if the sender cannot sign.
    /// </summary>
    SharedSecret,
}

/// <summary>
/// HTTP call to <c>POST {prefix}/hooks/{id}</c>. Requires <see cref="Secret"/> (at least 16 characters; generate one with
/// <see cref="WebhookSignature.CreateSecret"/>) and, by default, a timestamped signature
/// (<see cref="WebhookVerification.TimestampedSignature"/>). To accept unauthenticated webhooks you must explicitly set
/// <see cref="AllowUnauthenticated"/> = true.
/// </summary>
public sealed record WebhookTrigger(string? Secret = null) : AutomationTrigger
{
    /// <summary>Minimum length of the secret.</summary>
    public const int MinimumSecretLength = 16;

    /// <summary>How requests are authenticated. Defaults to <see cref="WebhookVerification.TimestampedSignature"/>.</summary>
    public WebhookVerification Verification { get; init; } = WebhookVerification.TimestampedSignature;

    /// <summary>
    /// Accepts requests without a secret: anyone who knows the URL can trigger the workflow with whatever content they want.
    /// Only for testing or trusted networks; in that case, use a random automation id (<see cref="Automation.Create{TInput}"/>).
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

/// <summary>Headers and signatures of <see cref="WebhookTrigger"/> webhooks.</summary>
public static class WebhookSignature
{
    /// <summary>Signature: <c>sha256={hex}</c>.</summary>
    public const string SignatureHeader = "X-Signature-256";

    /// <summary>Signature with the name GitHub uses (body only).</summary>
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";

    /// <summary>Timestamp of the signature, in Unix seconds.</summary>
    public const string TimestampHeader = "X-Webhook-Timestamp";

    /// <summary>Unique delivery id (deduplicates retries).</summary>
    public const string DeliveryIdHeader = "X-Webhook-Id";

    /// <summary>Delivery id with the name GitHub uses.</summary>
    public const string GitHubDeliveryHeader = "X-GitHub-Delivery";

    /// <summary>Shared secret (<see cref="WebhookVerification.SharedSecret"/>).</summary>
    public const string SecretHeader = "X-Webhook-Secret";

    /// <summary>Generates a random 256-bit secret (64 hexadecimal characters).</summary>
    public static string CreateSecret() => RandomNumberGenerator.GetHexString(64, lowercase: true);

    /// <summary>
    /// Signature for <see cref="WebhookVerification.TimestampedSignature"/>: <c>sha256=</c> + hexadecimal HMAC-SHA256 of
    /// <c>"{timestamp}.{body}"</c>. Use it in the sender (or in tests) together with the <see cref="TimestampHeader"/> header.
    /// </summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
    }

    /// <summary>Signature for <see cref="WebhookVerification.BodySignature"/> (body only, as GitHub does).</summary>
    public static string SignBody(string secret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Compares a received signature with the expected one in constant time.</summary>
    public static bool Matches(string expected, string? received) =>
        received is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received.Trim().ToLowerInvariant()), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Incoming webhook request: body and relevant headers.</summary>
/// <param name="Body">Body exactly as it arrived (the signature is computed over these bytes).</param>
public sealed record WebhookRequest(ReadOnlyMemory<byte> Body)
{
    /// <summary><see cref="WebhookSignature.SignatureHeader"/> header (or GitHub's).</summary>
    public string? Signature { get; init; }

    /// <summary><see cref="WebhookSignature.TimestampHeader"/> header.</summary>
    public string? Timestamp { get; init; }

    /// <summary><see cref="WebhookSignature.DeliveryIdHeader"/> header (or GitHub's).</summary>
    public string? DeliveryId { get; init; }

    /// <summary><see cref="WebhookSignature.SecretHeader"/> header.</summary>
    public string? Secret { get; init; }

    /// <summary>Builds the request by reading the documented headers with <paramref name="header"/> (name → value or null).</summary>
    public static WebhookRequest FromHeaders(ReadOnlyMemory<byte> body, Func<string, string?> header) => new(body)
    {
        Signature = NotEmpty(header(WebhookSignature.SignatureHeader)) ?? NotEmpty(header(WebhookSignature.GitHubSignatureHeader)),
        Timestamp = NotEmpty(header(WebhookSignature.TimestampHeader)),
        DeliveryId = NotEmpty(header(WebhookSignature.DeliveryIdHeader)) ?? NotEmpty(header(WebhookSignature.GitHubDeliveryHeader)),
        Secret = NotEmpty(header(WebhookSignature.SecretHeader)),
    };

    private static string? NotEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>Automation: when the trigger fires, the workflow runs with the given input.</summary>
public sealed record Automation
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required AutomationTrigger Trigger { get; init; }

    /// <summary>Name of the registered workflow to run.</summary>
    public required string Workflow { get; init; }

    /// <summary>Fixed input. If null, the event or webhook payload is used.</summary>
    public JsonElement? Input { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? NextRunAt { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public string? LastRunId { get; init; }

    /// <summary>User or agent that created it (to limit who can see or cancel it).</summary>
    public string? Owner { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Consecutive failures when firing it (unregistered workflow, nonexistent time zone, store down...). Each failure
    /// postpones the next attempt; when <see cref="WorkflowEngineOptions.MaxAutomationFailures"/> is reached, it is disabled.
    /// </summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Message of the last failure (null if the last firing succeeded).</summary>
    public string? LastError { get; init; }

    /// <summary>Version for optimistic concurrency (managed by the store; 0 if not saved yet).</summary>
    public long Version { get; init; }

    /// <summary>
    /// Creates an automation with typed input. If <paramref name="id"/> is not specified, one is generated with 128 random
    /// bits (so a webhook URL cannot be guessed).
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
