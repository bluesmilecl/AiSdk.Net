using System.Text;
using AiSdk.Artifacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace AiSdk.AspNetCore;

/// <summary>Opciones de <see cref="ArtifactEndpoints.MapArtifacts"/>.</summary>
public sealed class ArtifactEndpointOptions : EndpointSecurityOptions
{
    /// <summary>
    /// Decide si la petición puede ver un artifact (cada versión se comprueba por separado). Por defecto solo su dueño:
    /// <see cref="Artifact.Owner"/> igual a <see cref="EndpointSecurityOptions.ResolveUser"/>; los artifacts sin dueño no
    /// se sirven. Úselo para reglas propias (equipos, administradores, conversaciones compartidas...).
    /// </summary>
    public Func<HttpContext, Artifact, ValueTask<bool>>? CanAccess { get; init; }
}

/// <summary>Endpoints para ver y descargar los artifacts creados por los agentes.</summary>
public static class ArtifactEndpoints
{
    /// <summary>
    /// Publica los artifacts:
    /// <list type="bullet">
    /// <item><c>GET {prefix}?conversationId=</c>: lista de metadatos de una conversación; sin <c>conversationId</c>, los
    /// artifacts del usuario actual. Nunca lista todo el almacén.</item>
    /// <item><c>GET {prefix}/{id}</c>: página renderizada (o descarga si es un archivo)</item>
    /// <item><c>GET {prefix}/{id}/raw</c>: contenido original</item>
    /// <item><c>GET {prefix}/{id}/versions</c> y <c>{prefix}/{id}/v/{version}</c>: historial</item>
    /// </list>
    /// Exigen autorización por defecto y cada artifact pasa por <see cref="ArtifactEndpointOptions.CanAccess"/> (solo su
    /// dueño por defecto); los que no se pueden ver responden 404.
    /// Todas las respuestas llevan CSP <c>sandbox</c> (<see cref="ArtifactRenderer.ContentSecurityPolicy"/>) y
    /// <c>nosniff</c>: el contenido corre en un origen opaco, sin acceso a las cookies ni al DOM de la app. El HTML, SVG o
    /// XML crudo y los archivos se descargan como adjuntos. Aun así, lo más seguro es servir los artifacts desde un
    /// dominio propio (p. ej. <c>artifacts.example.com</c>), separado del de la aplicación.
    /// El almacén se toma de <paramref name="store"/> o del contenedor (<see cref="IArtifactStore"/>).
    /// </summary>
    public static RouteGroupBuilder MapArtifacts(this IEndpointRouteBuilder endpoints, string prefix = "/artifacts", IArtifactStore? store = null, ArtifactEndpointOptions? options = null)
    {
        options ??= new ArtifactEndpointOptions();
        var group = endpoints.MapGroup(prefix);
        EndpointSecurity.ApplyAuthorization(group, options);
        IArtifactStore Store(HttpContext http) => store ?? http.RequestServices.GetRequiredService<IArtifactStore>();

        ValueTask<bool> CanAccessAsync(HttpContext http, Artifact artifact)
        {
            if (options.CanAccess is { } canAccess)
            {
                return canAccess(http, artifact);
            }

            var user = options.ResolveUser(http);
            return ValueTask.FromResult(user is not null && new ArtifactScope(null, user).CanAccess(artifact));
        }

        async Task<Artifact?> FindAsync(HttpContext http, string id, int? version)
        {
            Artifact? artifact;
            try
            {
                artifact = await Store(http).GetAsync(id, version, http.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return null; // id inválido para el almacén
            }

            return artifact is not null && await CanAccessAsync(http, artifact).ConfigureAwait(false) ? artifact : null;
        }

        group.MapGet("/", async (HttpContext http, string? conversationId) =>
        {
            // Sin conversación se listan solo los artifacts del usuario actual: nunca todo el almacén.
            var user = options.ResolveUser(http);
            if (string.IsNullOrEmpty(conversationId) && user is null)
            {
                return Results.Problem(detail: "Indique conversationId.", statusCode: StatusCodes.Status400BadRequest);
            }

            var items = await Store(http).ListAsync(string.IsNullOrEmpty(conversationId) ? null : conversationId, http.RequestAborted).ConfigureAwait(false);
            var visible = new List<object>();
            foreach (var artifact in items)
            {
                if ((!string.IsNullOrEmpty(conversationId) || artifact.Owner == user) && await CanAccessAsync(http, artifact).ConfigureAwait(false))
                {
                    visible.Add(Summary(artifact));
                }
            }

            return Results.Ok(visible);
        });

        group.MapGet("/{id}", async (HttpContext http, string id) => Render(http, await FindAsync(http, id, null).ConfigureAwait(false), raw: false));
        group.MapGet("/{id}/raw", async (HttpContext http, string id) => Render(http, await FindAsync(http, id, null).ConfigureAwait(false), raw: true));
        group.MapGet("/{id}/v/{version:int}", async (HttpContext http, string id, int version) => Render(http, await FindAsync(http, id, version).ConfigureAwait(false), raw: false));
        group.MapGet("/{id}/versions", async (HttpContext http, string id) =>
        {
            if (await FindAsync(http, id, null).ConfigureAwait(false) is null)
            {
                return Results.NotFound();
            }

            var visible = new List<object>();
            foreach (var version in await Store(http).GetVersionsAsync(id, http.RequestAborted).ConfigureAwait(false))
            {
                if (await CanAccessAsync(http, version).ConfigureAwait(false))
                {
                    visible.Add(Summary(version));
                }
            }

            return Results.Ok(visible);
        });
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

        // Todo lo que sale de aquí lo escribió el modelo: origen opaco (sandbox) y sin adivinar el tipo.
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = ArtifactRenderer.ContentSecurityPolicy;
        var mediaType = SafeMediaType(artifact.EffectiveMediaType);
        if (artifact.Data is not null)
        {
            return Results.File(artifact.Data, mediaType, DownloadName(artifact, mediaType));
        }

        if (raw)
        {
            // El HTML, SVG o XML crudo se descarga: abierto en una pestaña se ejecutaría en el origen de la app.
            return IsActiveContent(mediaType)
                ? Results.File(Encoding.UTF8.GetBytes(artifact.Content), mediaType + "; charset=utf-8", DownloadName(artifact, mediaType))
                : Results.Text(artifact.Content, mediaType + "; charset=utf-8");
        }

        return Results.Content(ArtifactRenderer.ToHtml(artifact), "text/html; charset=utf-8");
    }

    /// <summary>Tipo de medio sin parámetros y en minúsculas; si no es válido, <c>application/octet-stream</c>.</summary>
    internal static string SafeMediaType(string? mediaType) =>
        MediaTypeHeaderValue.TryParse(mediaType, out var parsed) && parsed.MediaType.Value is { } value && !value.Contains('*', StringComparison.Ordinal)
            ? value.ToLowerInvariant()
            : "application/octet-stream";

    /// <summary>Tipos que el navegador ejecuta como documento (scripts, eventos): HTML, SVG y XML.</summary>
    internal static bool IsActiveContent(string mediaType)
    {
        var slash = mediaType.IndexOf('/', StringComparison.Ordinal);
        var subtype = slash < 0 ? mediaType : mediaType[(slash + 1)..];
        return subtype is "html" or "xhtml" or "javascript" or "ecmascript"
               || subtype.Contains("xml", StringComparison.Ordinal)
               || subtype.Contains("xsl", StringComparison.Ordinal)
               || mediaType.StartsWith("multipart/", StringComparison.Ordinal);
    }

    private static string DownloadName(Artifact artifact, string mediaType)
    {
        if (!string.IsNullOrWhiteSpace(artifact.FileName))
        {
            return artifact.FileName;
        }

        var extension = mediaType switch
        {
            "text/html" or "application/xhtml+xml" => ".html",
            "image/svg+xml" => ".svg",
            "text/markdown" => ".md",
            "application/json" => ".json",
            "text/csv" => ".csv",
            "application/pdf" => ".pdf",
            _ when mediaType.Contains("xml", StringComparison.Ordinal) => ".xml",
            _ => artifact.Data is null ? ".txt" : ".bin",
        };
        return artifact.Id + extension;
    }
}
