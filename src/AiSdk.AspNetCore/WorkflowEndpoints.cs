using System.Text.Json;
using AiSdk.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.AspNetCore;

/// <summary>Endpoints HTTP del motor de workflows.</summary>
public static class WorkflowEndpoints
{
    /// <summary>
    /// Publica el motor de workflows:
    /// <list type="bullet">
    /// <item><c>POST {prefix}/{workflow}/start</c>: inicia (cuerpo = entrada JSON; <c>?wait=true</c> espera el resultado)</item>
    /// <item><c>GET {prefix}/runs</c> y <c>GET {prefix}/runs/{id}</c>: estado, pasos, salida y registro</item>
    /// <item><c>POST {prefix}/runs/{id}/cancel</c> y <c>/retry</c></item>
    /// <item><c>POST {prefix}/events/{evento}?key=</c>: envía un evento (aprobaciones, pagos confirmados...)</item>
    /// <item><c>GET/POST {prefix}/automations</c>, <c>DELETE {prefix}/automations/{id}</c>, <c>POST .../{id}/run</c></item>
    /// <item><c>POST {prefix}/hooks/{id}</c>: webhooks entrantes (validan secreto o firma HMAC)</item>
    /// </list>
    /// Proteja el grupo devuelto con <c>.RequireAuthorization()</c>; los webhooks se autentican con su secreto,
    /// así que puede publicarlos aparte con <see cref="MapWorkflowWebhooks"/>.
    /// </summary>
    public static RouteGroupBuilder MapWorkflows(this IEndpointRouteBuilder endpoints, string prefix = "/workflows")
    {
        var group = endpoints.MapGroup(prefix);
        static WorkflowEngine Engine(HttpContext http) => http.RequestServices.GetRequiredService<WorkflowEngine>();

        group.MapPost("/{workflow}/start", async (HttpContext http, string workflow, bool? wait, string? runId) =>
        {
            var input = await ReadJsonAsync(http).ConfigureAwait(false);
            var run = await Engine(http).StartAsync(workflow, input, runId, "http", wait ?? false, http.RequestAborted).ConfigureAwait(false);
            return Results.Accepted($"{prefix}/runs/{run.Id}", Summary(run));
        });

        group.MapGet("/runs", async (HttpContext http, WorkflowStatus? status, string? workflow, int? limit) =>
            Results.Ok((await Engine(http).Store.ListRunsAsync(status, workflow, limit ?? 50, http.RequestAborted).ConfigureAwait(false)).Select(Summary)));

        group.MapGet("/runs/{id}", async (HttpContext http, string id) =>
            await Engine(http).GetRunAsync(id, http.RequestAborted).ConfigureAwait(false) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapPost("/runs/{id}/cancel", async (HttpContext http, string id) => Results.Ok(Summary(await Engine(http).CancelAsync(id, http.RequestAborted).ConfigureAwait(false))));
        group.MapPost("/runs/{id}/retry", async (HttpContext http, string id) => Results.Ok(Summary(await Engine(http).RetryAsync(id, http.RequestAborted).ConfigureAwait(false))));

        group.MapPost("/events/{eventName}", async (HttpContext http, string eventName, string? key) =>
        {
            var payload = await ReadJsonAsync(http).ConfigureAwait(false);
            var runs = await Engine(http).SendEventAsync(eventName, payload, key, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { runs });
        });

        group.MapGet("/automations", async (HttpContext http) => Results.Ok(await Engine(http).ListAutomationsAsync(http.RequestAborted).ConfigureAwait(false)));
        group.MapPost("/automations", async (HttpContext http, Automation automation) =>
            Results.Ok(await Engine(http).AddAutomationAsync(automation, http.RequestAborted).ConfigureAwait(false)));
        group.MapDelete("/automations/{id}", async (HttpContext http, string id) =>
        {
            await Engine(http).RemoveAutomationAsync(id, http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });
        group.MapPost("/automations/{id}/run", async (HttpContext http, string id) =>
            Results.Ok(Summary(await Engine(http).RunAutomationNowAsync(id, http.RequestAborted).ConfigureAwait(false))));

        MapHook(group);
        return group;
    }

    /// <summary>Publica solo los webhooks entrantes (<c>POST {prefix}/hooks/{id}</c>), sin autenticación de usuario.</summary>
    public static RouteGroupBuilder MapWorkflowWebhooks(this IEndpointRouteBuilder endpoints, string prefix = "/workflows")
    {
        var group = endpoints.MapGroup(prefix);
        MapHook(group);
        return group;
    }

    private static void MapHook(RouteGroupBuilder group) =>
        group.MapPost("/hooks/{id}", async (HttpContext http, string id) =>
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
            try
            {
                var run = await http.RequestServices.GetRequiredService<WorkflowEngine>().TriggerWebhookAsync(
                    id,
                    buffer.ToArray(),
                    http.Request.Headers["X-Webhook-Secret"].FirstOrDefault(),
                    http.Request.Headers["X-Signature-256"].FirstOrDefault() ?? http.Request.Headers["X-Hub-Signature-256"].FirstOrDefault(),
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

    private static async Task<JsonElement> ReadJsonAsync(HttpContext http)
    {
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
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
}
