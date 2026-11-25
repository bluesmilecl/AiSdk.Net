using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AiSdk.AspNetCore;

/// <summary>
/// Common security options for the AiSdk endpoints (<see cref="ArtifactEndpoints"/>,
/// <see cref="WorkflowEndpoints"/> and <see cref="SkillEndpoints"/>).
/// </summary>
/// <remarks>
/// The endpoints require authorization by default: <c>RequireAuthorization()</c> is applied to the group (with the
/// <see cref="AuthorizationPolicies"/>, if any). If the application has not configured authentication or authorization,
/// requests fail instead of being left open. Exposing them without authentication must be requested explicitly with
/// <see cref="AllowAnonymous"/>.
/// </remarks>
public abstract class EndpointSecurityOptions
{
    /// <summary>
    /// Does not require authorization on the group (does not call <c>RequireAuthorization()</c>). Defaults to <c>false</c>.
    /// Use it only for demos, tests or trusted networks: the fallback policy (<c>FallbackPolicy</c>) and any
    /// authorization you add on the returned group still apply.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>Authorization policies required on the group. Empty: the default policy (authenticated user).</summary>
    public IReadOnlyList<string> AuthorizationPolicies { get; init; } = [];

    /// <summary>
    /// Gets the id of the request's user, used as the owner of artifacts, runs and automations.
    /// Defaults to <see cref="EndpointSecurity.GetUserId"/>. It must come from the server's authentication, never from
    /// the body, the query string or a header the client controls.
    /// </summary>
    public Func<HttpContext, string?> ResolveUser { get; init; } = EndpointSecurity.GetUserId;
}

/// <summary>Security helpers shared by the AiSdk endpoints.</summary>
public static class EndpointSecurity
{
    /// <summary>
    /// Id of the authenticated user: the <see cref="ClaimTypes.NameIdentifier"/> claim, then <c>sub</c> and finally
    /// <c>Identity.Name</c>. Returns <c>null</c> if the request is anonymous.
    /// </summary>
    public static string? GetUserId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.Identity.Name;
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>Applies the default authorization to the group, unless anonymous access was requested.</summary>
    internal static void ApplyAuthorization(RouteGroupBuilder group, EndpointSecurityOptions options)
    {
        if (options.AllowAnonymous)
        {
            return;
        }

        if (options.AuthorizationPolicies.Count == 0)
        {
            group.RequireAuthorization();
        }
        else
        {
            group.RequireAuthorization([.. options.AuthorizationPolicies]);
        }
    }

    /// <summary>
    /// Requires <c>Content-Type: application/json</c> (or <c>*/*+json</c>) on the group's POST/PUT/PATCH requests; otherwise, 415.
    /// A form or a "simple" <c>fetch</c> from another site cannot send that type without a CORS preflight request,
    /// so this prevents cookie-based CSRF even when the body is valid JSON.
    /// </summary>
    internal static void RequireJsonRequests(RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            if ((HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method))
                && !request.HasJsonContentType())
            {
                return Results.Problem(
                    detail: "Content-Type debe ser application/json (también en peticiones sin cuerpo).",
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            return await next(context).ConfigureAwait(false);
        });

    /// <summary>403 with a readable detail for the API integrator.</summary>
    internal static IResult Forbidden(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden);
}
