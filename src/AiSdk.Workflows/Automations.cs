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
}

/// <summary>
/// Expresión cron de 5 campos (minuto hora día mes día-semana) o 6 con segundos, en la zona horaria
/// indicada (id IANA o de Windows, p. ej. <c>"America/Santiago"</c>). Ejemplo: <c>"0 9 * * 1-5"</c>.
/// </summary>
public sealed record CronTrigger(string Expression, string? TimeZone = null) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun)
    {
        var expression = Parse(Expression);
        var zone = TimeZone is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        return expression.GetNextOccurrence(after, zone);
    }

    public override string Describe() => $"cron \"{Expression}\"{(TimeZone is null ? " (UTC)" : $" ({TimeZone})")}";

    /// <summary>Valida y parsea la expresión (5 o 6 campos).</summary>
    public static CronExpression Parse(string expression) =>
        CronExpression.Parse(expression, expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
}

/// <summary>Cada cierto intervalo.</summary>
public sealed record IntervalTrigger(TimeSpan Every) : AutomationTrigger
{
    public override DateTimeOffset? NextOccurrence(DateTimeOffset after, DateTimeOffset? lastRun) =>
        lastRun is { } last && last + Every > after ? last + Every : after + Every;

    public override string Describe() => $"cada {Every}";
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

/// <summary>
/// Llamada HTTP a <c>POST {prefijo}/hooks/{id}</c>. Con <see cref="Secret"/>, la petición debe traer
/// la cabecera <c>X-Webhook-Secret</c> o una firma HMAC-SHA256 del cuerpo en <c>X-Signature-256: sha256=...</c>.
/// </summary>
public sealed record WebhookTrigger(string? Secret = null) : AutomationTrigger
{
    public override string Describe() => "webhook";
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

    /// <summary>Crea una automatización con entrada tipada.</summary>
    public static Automation Create<TInput>(string name, AutomationTrigger trigger, string workflow, TInput input, string? id = null) => new()
    {
        Id = id ?? Artifacts.ArtifactTools.Slug(name) + "-" + Guid.NewGuid().ToString("N")[..6],
        Name = name,
        Trigger = trigger,
        Workflow = workflow,
        Input = JsonSerializer.SerializeToElement(input, JsonSchema.DefaultSerializerOptions),
    };
}
