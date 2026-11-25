using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AiSdk.AspNetCore;

/// <summary>
/// Opciones de seguridad comunes de los endpoints de AiSdk (<see cref="ArtifactEndpoints"/>,
/// <see cref="WorkflowEndpoints"/> y <see cref="SkillEndpoints"/>).
/// </summary>
/// <remarks>
/// Los endpoints exigen autorización por defecto: se aplica <c>RequireAuthorization()</c> al grupo (con
/// <see cref="AuthorizationPolicies"/> si se indican). Si la aplicación no configuró autenticación ni autorización,
/// las peticiones fallan en lugar de quedar abiertas. Publicarlos sin autenticación exige pedirlo con
/// <see cref="AllowAnonymous"/>.
/// </remarks>
public abstract class EndpointSecurityOptions
{
    /// <summary>
    /// No exige autorización en el grupo (no llama a <c>RequireAuthorization()</c>). Por defecto <c>false</c>.
    /// Úselo solo en demos, pruebas o redes de confianza: la política de respaldo (<c>FallbackPolicy</c>) y la
    /// autorización que agregue sobre el grupo devuelto siguen aplicando.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>Políticas de autorización exigidas al grupo. Vacía: la política por defecto (usuario autenticado).</summary>
    public IReadOnlyList<string> AuthorizationPolicies { get; init; } = [];

    /// <summary>
    /// Obtiene el id del usuario de la petición, que se usa como dueño de artifacts, ejecuciones y automatizaciones.
    /// Por defecto <see cref="EndpointSecurity.GetUserId"/>. Debe salir de la autenticación del servidor, nunca del
    /// cuerpo, la consulta o una cabecera que controle el cliente.
    /// </summary>
    public Func<HttpContext, string?> ResolveUser { get; init; } = EndpointSecurity.GetUserId;
}

/// <summary>Utilidades de seguridad compartidas por los endpoints de AiSdk.</summary>
public static class EndpointSecurity
{
    /// <summary>
    /// Id del usuario autenticado: claim <see cref="ClaimTypes.NameIdentifier"/>, luego <c>sub</c> y por último
    /// <c>Identity.Name</c>. Devuelve <c>null</c> si la petición es anónima.
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

    /// <summary>Aplica la autorización por defecto al grupo, salvo que se haya pedido acceso anónimo.</summary>
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
    /// Exige <c>Content-Type: application/json</c> (o <c>*/*+json</c>) en los POST/PUT/PATCH del grupo; si no, 415.
    /// Un formulario o un <c>fetch</c> "simple" de otro sitio no puede enviar ese tipo sin una petición previa CORS,
    /// así que esto evita CSRF con cookies aunque el cuerpo sea JSON válido.
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

    /// <summary>403 con un detalle legible para quien integra la API.</summary>
    internal static IResult Forbidden(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden);
}
