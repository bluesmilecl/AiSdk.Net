using System.Text;
using AiSdk.Artifacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace AiSdk.AspNetCore;

/// <summary>Options for <see cref="ArtifactEndpoints.MapArtifacts"/>.</summary>
public sealed class ArtifactEndpointOptions : EndpointSecurityOptions
{
    /// <summary>
    /// Decides whether the request may view an artifact (each version is checked separately). By default only its owner:
    /// <see cref="Artifact.Owner"/> equal to <see cref="EndpointSecurityOptions.ResolveUser"/>; artifacts without an owner are not
    /// served. Use it for custom rules (teams, administrators, shared conversations...).
    /// </summary>
    public Func<HttpContext, Artifact, ValueTask<bool>>? CanAccess { get; init; }
}

/// <summary>Endpoints for viewing and downloading the artifacts created by agents.</summary>
public static class ArtifactEndpoints
{
    /// <summary>
    /// Maps the artifacts:
    /// <list type="bullet">
    /// <item><c>GET {prefix}?conversationId=</c>: metadata list for a conversation; without <c>conversationId</c>, the
    /// current user's artifacts. It never lists the whole store.</item>
    /// <item><c>GET {prefix}/{id}</c>: rendered page (or a download if it is a file)</item>
    /// <item><c>GET {prefix}/{id}/raw</c>: original content</item>
    /// <item><c>GET {prefix}/{id}/versions</c> and <c>{prefix}/{id}/v/{version}</c>: history</item>
    /// </list>
    /// They require authorization by default and each artifact goes through <see cref="ArtifactEndpointOptions.CanAccess"/> (only its
    /// owner by default); those that cannot be viewed respond 404.
    /// All responses carry a CSP <c>sandbox</c> (<see cref="ArtifactRenderer.ContentSecurityPolicy"/>) and
    /// <c>nosniff</c>: the content runs in an opaque origin, with no access to the app's cookies or DOM. Raw HTML, SVG or
    /// XML and files are downloaded as attachments. Even so, the safest option is to serve the artifacts from a
    /// dedicated domain (e.g. <c>artifacts.example.com</c>), separate from the application's.
    /// The store is taken from <paramref name="store"/> or from the container (<see cref="IArtifactStore"/>).
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
                return Results.Problem(detail: Resources.Strings.Artifact_ConversationIdRequired, statusCode: StatusCodes.Status400BadRequest);
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

    /// <summary>Media type without parameters and in lowercase; if invalid, <c>application/octet-stream</c>.</summary>
    internal static string SafeMediaType(string? mediaType) =>
        MediaTypeHeaderValue.TryParse(mediaType, out var parsed) && parsed.MediaType.Value is { } value && !value.Contains('*', StringComparison.Ordinal)
            ? value.ToLowerInvariant()
            : "application/octet-stream";

    /// <summary>Types the browser executes as a document (scripts, events): HTML, SVG and XML.</summary>
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
