using System.Text.Json;
using AiSdk.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.AspNetCore;

/// <summary>Security options for <see cref="WorkflowEndpoints.MapWorkflows"/>.</summary>
public sealed class WorkflowEndpointOptions : EndpointSecurityOptions
{
    /// <summary>
    /// Decides whether the request may start the given workflow (also when creating or editing automations that use it).
    /// By default any authorized user can start any registered workflow: restrict it if there are internal workflows
    /// (e.g. <c>agent-task</c>, whose input specifies the owner).
    /// </summary>
    public Func<HttpContext, string, ValueTask<bool>>? CanStart { get; init; }

    /// <summary>
    /// Decides whether the request may view, cancel or retry a run. By default only its owner, according to
    /// <see cref="WorkflowEndpoints.GetRunOwnerAsync"/>: whoever started it over HTTP or the owner of the automation that
    /// triggered it. Runs without an owner (started from code or by events) are not shown.
    /// </summary>
    public Func<HttpContext, WorkflowRun, ValueTask<bool>>? CanAccessRun { get; init; }

    /// <summary>
    /// Decides whether the request may view, modify, delete or run an automation. By default only its owner
    /// (<see cref="Automation.Owner"/>); automations declared in code (without an owner) are not shown.
    /// </summary>
    public Func<HttpContext, Automation, ValueTask<bool>>? CanAccessAutomation { get; init; }

    /// <summary>
    /// Decides whether the request may send an event (e.g. an approval), given the runs waiting for it.
    /// Required to use <c>POST {prefix}/events/{eventName}</c>: without it the endpoint returns 403, because who may approve
    /// depends on each application (usually not whoever started the run) and an event also triggers
    /// automations with an <see cref="EventTrigger"/>.
    /// </summary>
    public Func<HttpContext, WorkflowEventRequest, ValueTask<bool>>? CanSendEvent { get; init; }

    /// <summary>
    /// Includes the incoming webhooks (<c>POST {prefix}/hooks/{id}</c>). Defaults to <c>true</c>. They do not require a user:
    /// they authenticate with the secret or signature of their <see cref="WebhookTrigger"/>.
    /// </summary>
    public bool MapWebhooks { get; init; } = true;
}

/// <summary>Event to be sent over HTTP and the runs waiting for it (which it would resume).</summary>
/// <param name="EventName">Event name.</param>
/// <param name="Key">Correlation key (<c>?key=</c>), e.g. the order id.</param>
/// <param name="Payload">JSON body of the event.</param>
/// <param name="WaitingRuns">Runs waiting for this event with this key.</param>
public sealed record WorkflowEventRequest(string EventName, string? Key, JsonElement Payload, IReadOnlyList<WorkflowRun> WaitingRuns);

/// <summary>HTTP endpoints for the workflow engine.</summary>
public static class WorkflowEndpoints
{
    /// <summary>Prefix of <see cref="WorkflowRun.TriggeredBy"/> for runs started by a user over HTTP.</summary>
    public const string UserTriggerPrefix = "user:";

    /// <summary>
    /// Maximum size (bytes) of the request body for these endpoints, webhooks included: 1 MiB. A larger body
    /// gets a 413 without being read in full (webhooks are anonymous: they must not be able to fill memory).
    /// </summary>
    public const int MaxRequestBodyBytes = 1 << 20;

    // Prefijos que usa el motor para las ejecuciones disparadas por una automatización (seguidos de su id).
    private static readonly string[] AutomationTriggerPrefixes = ["schedule:", "webhook:", "manual:"];

    /// <summary>
    /// Maps the workflow engine:
    /// <list type="bullet">
    /// <item><c>POST {prefix}/{workflow}/start</c>: starts a run (body = JSON input; <c>?wait=true</c> waits for the result)</item>
    /// <item><c>GET {prefix}/runs</c> and <c>GET {prefix}/runs/{id}</c>: status, steps, output and log</item>
    /// <item><c>POST {prefix}/runs/{id}/cancel</c> and <c>/retry</c></item>
    /// <item><c>POST {prefix}/events/{eventName}?key=</c>: sends an event (approvals, confirmed payments...)</item>
    /// <item><c>GET/POST {prefix}/automations</c>, <c>DELETE {prefix}/automations/{id}</c>, <c>POST .../{id}/run</c></item>
    /// <item><c>POST {prefix}/hooks/{id}</c>: incoming webhooks (they validate a secret or HMAC signature)</item>
    /// </list>
    /// Secure by default: authorization is required (except for webhooks), each run and automation is visible only to its
    /// owner (<see cref="WorkflowEndpointOptions.CanAccessRun"/>, <see cref="WorkflowEndpointOptions.CanAccessAutomation"/>),
    /// the owner is taken from authentication and never from the body, events require
    /// <see cref="WorkflowEndpointOptions.CanSendEvent"/>, and POST requests require <c>Content-Type: application/json</c> (415 otherwise)
    /// to prevent CSRF from other sites' forms.
    /// </summary>
    public static RouteGroupBuilder MapWorkflows(this IEndpointRouteBuilder endpoints, string prefix = "/workflows", WorkflowEndpointOptions? options = null)
    {
        options ??= new WorkflowEndpointOptions();
        var group = endpoints.MapGroup(prefix).AddEndpointFilter(BadRequestFilter);
        var api = group.MapGroup(string.Empty);
        EndpointSecurity.ApplyAuthorization(api, options);
        EndpointSecurity.RequireJsonRequests(api);
        static WorkflowEngine Engine(HttpContext http) => http.RequestServices.GetRequiredService<WorkflowEngine>();
        RunAccess Access(HttpContext http) => new(http, Engine(http), options);

        async ValueTask<bool> CanStartAsync(HttpContext http, string workflow) =>
            options.CanStart is null || await options.CanStart(http, workflow).ConfigureAwait(false);

        api.MapPost("/{workflow}/start", async (HttpContext http, string workflow, bool? wait, string? runId) =>
        {
            var engine = Engine(http);
            if (!engine.Workflows.Any(w => w.Name == workflow))
            {
                return Results.NotFound();
            }

            if (!await CanStartAsync(http, workflow).ConfigureAwait(false))
            {
                return EndpointSecurity.Forbidden($"No puede iniciar el workflow '{workflow}'.");
            }

            // runId es una clave de idempotencia: si ya existe una ejecución con ese id, solo se devuelve a quien puede verla.
            var access = Access(http);
            if (runId is not null && await access.FindAsync(runId).ConfigureAwait(false) is var (existing, visible) && existing is not null)
            {
                return visible ? Results.Accepted($"{prefix}/runs/{existing.Id}", Summary(existing)) : Results.Conflict();
            }

            var input = await ReadJsonAsync(http).ConfigureAwait(false);
            var owner = options.ResolveUser(http);
            WorkflowRun run;
            try
            {
                run = await engine.StartAsync(workflow, input, runId, owner is null ? "http" : UserTriggerPrefix + owner, wait ?? false, http.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(); // id de ejecución inválido para el almacén
            }

            if (runId is not null && !await access.CanAccessAsync(run).ConfigureAwait(false))
            {
                return Results.Conflict(); // otra petición creó una ejecución con el mismo id
            }

            return Results.Accepted($"{prefix}/runs/{run.Id}", Summary(run));
        });

        api.MapGet("/runs", async (HttpContext http, WorkflowStatus? status, string? workflow, int? limit) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var access = Access(http);
            var visible = new List<object>();
            foreach (var run in await Engine(http).Store.ListRunsAsync(status, workflow, int.MaxValue, http.RequestAborted).ConfigureAwait(false))
            {
                if (visible.Count >= take)
                {
                    break;
                }

                if (await access.CanAccessAsync(run).ConfigureAwait(false))
                {
                    visible.Add(Summary(run));
                }
            }

            return Results.Ok(visible);
        });

        api.MapGet("/runs/{id}", async (HttpContext http, string id) =>
            await Access(http).FindAsync(id).ConfigureAwait(false) is ({ } run, true) ? Results.Ok(run) : Results.NotFound());

        api.MapPost("/runs/{id}/cancel", async (HttpContext http, string id) =>
            await Access(http).FindAsync(id).ConfigureAwait(false) is ({ } run, true)
                ? Results.Ok(Summary(await Engine(http).CancelAsync(run.Id, http.RequestAborted).ConfigureAwait(false)))
                : Results.NotFound());

        api.MapPost("/runs/{id}/retry", async (HttpContext http, string id) =>
            await Access(http).FindAsync(id).ConfigureAwait(false) is ({ } run, true)
                ? Results.Ok(Summary(await Engine(http).RetryAsync(run.Id, http.RequestAborted).ConfigureAwait(false)))
                : Results.NotFound());

        api.MapPost("/events/{eventName}", async (HttpContext http, string eventName, string? key) =>
        {
            if (options.CanSendEvent is null)
            {
                return EndpointSecurity.Forbidden("Los eventos por HTTP están deshabilitados: configure WorkflowEndpointOptions.CanSendEvent para decidir quién puede enviarlos.");
            }

            var payload = await ReadJsonAsync(http).ConfigureAwait(false);
            var engine = Engine(http);
            var waiting = await engine.Store.ListRunsWaitingForEventAsync(eventName, key, int.MaxValue, http.RequestAborted).ConfigureAwait(false);
            if (!await options.CanSendEvent(http, new WorkflowEventRequest(eventName, key, payload, waiting)).ConfigureAwait(false))
            {
                return EndpointSecurity.Forbidden($"No puede enviar el evento '{eventName}'.");
            }

            // Solo se reanudan las ejecuciones que vio CanSendEvent: una que empiece a esperar el evento entre la
            // autorización y el envío no queda aprobada por una decisión que no la incluía.
            var runs = await engine.SendEventToRunsAsync(eventName, waiting.Select(r => r.Id), payload, key, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { runs });
        });

        // Nota: con solo HttpContext como parámetro la lambda se tomaría como RequestDelegate y se descartaría el IResult.
        api.MapGet("/automations", async (HttpContext http, CancellationToken ct) =>
        {
            var visible = new List<Automation>();
            foreach (var automation in await Engine(http).ListAutomationsAsync(ct).ConfigureAwait(false))
            {
                if (await CanAccessAutomationAsync(http, automation, options).ConfigureAwait(false))
                {
                    visible.Add(Redact(automation));
                }
            }

            return Results.Ok(visible);
        });

        api.MapPost("/automations", async (HttpContext http, Automation automation) =>
        {
            var engine = Engine(http);
            if (!await CanStartAsync(http, automation.Workflow).ConfigureAwait(false))
            {
                return EndpointSecurity.Forbidden($"No puede iniciar el workflow '{automation.Workflow}'.");
            }

            if (automation.Trigger is WebhookTrigger { Secret: null or "" })
            {
                return Results.Problem(detail: "Un webhook creado por HTTP necesita un secreto.", statusCode: StatusCodes.Status400BadRequest);
            }

            Automation? existing;
            try
            {
                existing = await engine.Store.GetAutomationAsync(automation.Id, http.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }

            if (existing is not null && !await CanAccessAutomationAsync(http, existing, options).ConfigureAwait(false))
            {
                return Results.Conflict(); // el id ya lo usa una automatización de otro dueño
            }

            // El dueño y el historial los fija el servidor: lo que diga el cuerpo se ignora.
            var safe = automation with
            {
                Owner = existing is null ? options.ResolveUser(http) : existing.Owner,
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                LastRunAt = existing?.LastRunAt,
                LastRunId = existing?.LastRunId,
                NextRunAt = null,
            };
            try
            {
                return Results.Ok(Redact(await engine.AddAutomationAsync(safe, http.RequestAborted).ConfigureAwait(false)));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapDelete("/automations/{id}", async (HttpContext http, string id) =>
        {
            if (await FindAutomationAsync(http, id, options).ConfigureAwait(false) is not { } automation)
            {
                return Results.NotFound();
            }

            await Engine(http).RemoveAutomationAsync(automation.Id, http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapPost("/automations/{id}/run", async (HttpContext http, string id) =>
            await FindAutomationAsync(http, id, options).ConfigureAwait(false) is { } automation
                ? Results.Ok(Summary(await Engine(http).RunAutomationNowAsync(automation.Id, http.RequestAborted).ConfigureAwait(false)))
                : Results.NotFound());

        if (options.MapWebhooks)
        {
            MapHook(group);
        }

        return group;
    }

    /// <summary>Maps only the incoming webhooks (<c>POST {prefix}/hooks/{id}</c>), without user authentication.</summary>
    public static RouteGroupBuilder MapWorkflowWebhooks(this IEndpointRouteBuilder endpoints, string prefix = "/workflows")
    {
        var group = endpoints.MapGroup(prefix).AddEndpointFilter(BadRequestFilter);
        MapHook(group);
        return group;
    }

    /// <summary>
    /// Owner of a run: the user who started it over HTTP (<see cref="UserTriggerPrefix"/>) or the owner of the
    /// scheduled, webhook or manual automation that triggered it. <c>null</c> if it has none (started from code or by
    /// an event). Useful to combine with custom rules in <see cref="WorkflowEndpointOptions.CanAccessRun"/>.
    /// </summary>
    public static async Task<string?> GetRunOwnerAsync(WorkflowEngine engine, WorkflowRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(run);
        if (run.TriggeredBy is not { } triggeredBy)
        {
            return null;
        }

        if (triggeredBy.StartsWith(UserTriggerPrefix, StringComparison.Ordinal))
        {
            return triggeredBy[UserTriggerPrefix.Length..] is { Length: > 0 } user ? user : null;
        }

        foreach (var automationPrefix in AutomationTriggerPrefixes)
        {
            if (triggeredBy.StartsWith(automationPrefix, StringComparison.Ordinal))
            {
                try
                {
                    return (await engine.Store.GetAutomationAsync(triggeredBy[automationPrefix.Length..], cancellationToken).ConfigureAwait(false))?.Owner;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static async ValueTask<bool> CanAccessAutomationAsync(HttpContext http, Automation automation, WorkflowEndpointOptions options)
    {
        if (options.CanAccessAutomation is { } custom)
        {
            return await custom(http, automation).ConfigureAwait(false);
        }

        var user = options.ResolveUser(http);
        return user is not null && automation.Owner == user;
    }

    private static async Task<Automation?> FindAutomationAsync(HttpContext http, string id, WorkflowEndpointOptions options)
    {
        Automation? automation;
        try
        {
            automation = await http.RequestServices.GetRequiredService<WorkflowEngine>().Store.GetAutomationAsync(id, http.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return automation is not null && await CanAccessAutomationAsync(http, automation, options).ConfigureAwait(false) ? automation : null;
    }

    /// <summary>Hides the webhook secret when returning automations.</summary>
    private static Automation Redact(Automation automation) =>
        automation.Trigger is WebhookTrigger { Secret: { Length: > 0 } } ? automation with { Trigger = new WebhookTrigger("***") } : automation;

    private static void MapHook(RouteGroupBuilder group) =>
        group.MapPost("/hooks/{id}", async (HttpContext http, string id) =>
        {
            using var buffer = await ReadBodyAsync(http).ConfigureAwait(false);
            if (buffer is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            try
            {
                // Lee las cabeceras documentadas en WebhookSignature (firma, marca de tiempo, id de entrega y secreto).
                var run = await http.RequestServices.GetRequiredService<WorkflowEngine>().TriggerWebhookAsync(
                    id,
                    WebhookRequest.FromHeaders(buffer.ToArray(), name => http.Request.Headers[name].FirstOrDefault()),
                    http.RequestAborted).ConfigureAwait(false);
                return Results.Accepted(value: new { runId = run.Id });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

    /// <summary>Converts request errors (invalid JSON, body too large) into their HTTP status code.</summary>
    private static async ValueTask<object?> BadRequestFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    /// <summary>Reads the body up to <see cref="MaxRequestBodyBytes"/>; null if it exceeds it (without reading further).</summary>
    private static async Task<MemoryStream?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxRequestBodyBytes)
        {
            return null;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxRequestBodyBytes)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpContext http)
    {
        using var buffer = await ReadBodyAsync(http).ConfigureAwait(false)
                           ?? throw new BadHttpRequestException("El cuerpo supera el tamaño máximo permitido.", StatusCodes.Status413PayloadTooLarge);
        if (buffer.Length == 0)
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new BadHttpRequestException("El cuerpo debe ser JSON válido.", ex);
        }
    }

    private static object Summary(WorkflowRun run) => new
    {
        id = run.Id,
        workflow = run.WorkflowName,
        status = run.Status.ToString(),
        createdAt = run.CreatedAt,
        updatedAt = run.UpdatedAt,
        completedAt = run.CompletedAt,
        resumeAt = run.ResumeAt,
        waitingFor = run.Waiting?.EventName,
        triggeredBy = run.TriggeredBy,
        output = run.Output,
        error = run.Error,
    };

    /// <summary>Run access control for a request (caches automation owners).</summary>
    private sealed class RunAccess(HttpContext http, WorkflowEngine engine, WorkflowEndpointOptions options)
    {
        private readonly Dictionary<string, string?> _automationOwners = new(StringComparer.Ordinal);

        public async ValueTask<bool> CanAccessAsync(WorkflowRun run)
        {
            if (options.CanAccessRun is { } custom)
            {
                return await custom(http, run).ConfigureAwait(false);
            }

            var user = options.ResolveUser(http);
            if (user is null)
            {
                return false;
            }

            var key = run.TriggeredBy ?? string.Empty;
            if (!_automationOwners.TryGetValue(key, out var owner))
            {
                owner = await GetRunOwnerAsync(engine, run, http.RequestAborted).ConfigureAwait(false);
                _automationOwners[key] = owner;
            }

            return owner == user;
        }

        /// <summary>The run (or null if it does not exist or the id is invalid) and whether the request may view it.</summary>
        public async ValueTask<(WorkflowRun? Run, bool Visible)> FindAsync(string id)
        {
            WorkflowRun? run;
            try
            {
                run = await engine.GetRunAsync(id, http.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return (null, false);
            }

            return run is null ? (null, false) : (run, await CanAccessAsync(run).ConfigureAwait(false));
        }
    }
}
