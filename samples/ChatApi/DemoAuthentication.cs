using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChatApi;

/// <summary>
/// Autenticación de DEMOSTRACIÓN: <c>Authorization: Bearer &lt;token&gt;</c> con los usuarios de la sección
/// <c>DemoAuth:Users</c> (solo en appsettings.Development.json). En producción reemplácela por la suya (JWT, cookies,
/// Entra ID...): sin usuarios configurados nadie se autentica y los endpoints protegidos responden 401.
/// </summary>
/// <remarks>
/// Desde useChat: <c>new DefaultChatTransport({ api, headers: { Authorization: "Bearer dev-ana-..." } })</c>.
/// Al usar una cabecera (y no cookies) el navegador no la envía sola desde otros sitios: no hay CSRF.
/// </remarks>
public sealed class DemoAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DemoToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        foreach (var user in configuration.GetSection("DemoAuth:Users").GetChildren())
        {
            // Comparación en tiempo constante; se ignoran tokens demasiado cortos.
            if (user["Token"] is { Length: >= 16 } expected
                && user["Id"] is { Length: > 0 } id
                && CryptographicOperations.FixedTimeEquals(token, Encoding.UTF8.GetBytes(expected)))
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id) };
                claims.AddRange((user.GetSection("Roles").Get<string[]>() ?? []).Select(role => new Claim(ClaimTypes.Role, role)));
                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.Fail("Token inválido."));
    }
}
