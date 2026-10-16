using AiSdk.Artifacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.AspNetCore;

/// <summary>Endpoints para ver y descargar los artifacts creados por los agentes.</summary>
public static class ArtifactEndpoints
{
    /// <summary>
    /// Publica los artifacts:
    /// <list type="bullet">
    /// <item><c>GET {prefix}?conversationId=</c>: lista (JSON)</item>
    /// <item><c>GET {prefix}/{id}</c>: página renderizada (o descarga si es un archivo)</item>
    /// <item><c>GET {prefix}/{id}/raw</c>: contenido original</item>
    /// <item><c>GET {prefix}/{id}/versions</c> y <c>{prefix}/{id}/v/{version}</c>: historial</item>
    /// </list>
    /// Las páginas se sirven con CSP <c>sandbox</c>: corren en un origen aislado, sin acceso a las cookies de la app.
    /// El almacén se toma de <paramref name="store"/> o del contenedor (<see cref="IArtifactStore"/>).
    /// Agrega autorización con <c>.RequireAuthorization()</c> sobre el grupo devuelto.
    /// </summary>
    public static RouteGroupBuilder MapArtifacts(this IEndpointRouteBuilder endpoints, string prefix = "/artifacts", IArtifactStore? store = null)
    {
        var group = endpoints.MapGroup(prefix);
        IArtifactStore Store(HttpContext http) => store ?? http.RequestServices.GetRequiredService<IArtifactStore>();

        group.MapGet("/", async (HttpContext http, string? conversationId) =>
            Results.Ok((await Store(http).ListAsync(conversationId, http.RequestAborted)).Select(Summary)));

        group.MapGet("/{id}", async (HttpContext http, string id) => Render(http, await Store(http).GetAsync(id, cancellationToken: http.RequestAborted), raw: false));
        group.MapGet("/{id}/raw", async (HttpContext http, string id) => Render(http, await Store(http).GetAsync(id, cancellationToken: http.RequestAborted), raw: true));
        group.MapGet("/{id}/v/{version:int}", async (HttpContext http, string id, int version) => Render(http, await Store(http).GetAsync(id, version, http.RequestAborted), raw: false));
        group.MapGet("/{id}/versions", async (HttpContext http, string id) =>
            Results.Ok((await Store(http).GetVersionsAsync(id, http.RequestAborted)).Select(Summary)));
        return group;
    }

    private static object Summary(Artifact a) => new
    {
        id = a.Id,
        title = a.Title,
        type = a.Type,
        language = a.Language,
        version = a.Version,
        fileName = a.FileName,
        mediaType = a.EffectiveMediaType,
        createdAt = a.CreatedAt,
        conversationId = a.ConversationId,
    };

    private static IResult Render(HttpContext http, Artifact? artifact, bool raw)
    {
        if (artifact is null)
        {
            return Results.NotFound();
        }

        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (artifact.Data is not null)
        {
            return Results.File(artifact.Data, artifact.EffectiveMediaType, artifact.FileName);
        }

        if (raw)
        {
            return Results.Text(artifact.Content, artifact.EffectiveMediaType + "; charset=utf-8");
        }

        http.Response.Headers.ContentSecurityPolicy = ArtifactRenderer.ContentSecurityPolicy;
        return Results.Content(ArtifactRenderer.ToHtml(artifact), "text/html; charset=utf-8");
    }
}
