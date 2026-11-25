using System.Buffers.Text;
using System.Security.Cryptography;
using AiSdk.UI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.AspNetCore;

/// <summary>
/// Aprobación de herramientas con <c>useChat</c> (<c>addToolApprovalResponse</c>) en un endpoint de ASP.NET Core.
/// </summary>
/// <example>
/// <code>
/// api.MapPost("/chat", (UIChatRequest request, MyAgent agent, HttpContext http) =>
/// {
///     var chatId = request.Id ?? Guid.NewGuid().ToString("N");
///     var approvals = ToolApprovalProtection.ForChat(http, chatId);
///     var input = UIMessageConverter.ToModelInput(request.Messages, new() { ToolApprovals = approvals, ServerExecutedTools = ["send_email"] });
///     return agent.Stream(new AgentCallOptions
///     {
///         Messages = input.Messages,
///         ToolApprovals = input.ToolApprovals,
///         DeferToolApprovals = true,
///     }).ToUIMessageStreamResult(toolApprovals: approvals);
/// });
/// </code>
/// </example>
public static class ToolApprovalProtection
{
    private const string Purpose = "AiSdk.UI.ToolApproval.v1";

    /// <summary>
    /// Protector de approvalIds para un chat de un usuario, con ASP.NET Core Data Protection (claves rotadas por el
    /// framework; en varias instancias, comparta el anillo de claves como para las cookies). Los ids solo verifican para
    /// el mismo dueño (<paramref name="owner"/> o, si es null, el usuario autenticado) y el mismo
    /// <paramref name="chatId"/>, y caducan tras <paramref name="lifetime"/> (por defecto
    /// <see cref="ToolApprovalProtector.DefaultLifetime"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Si la petición no tiene dueño (anónima y sin <paramref name="owner"/>) o Data Protection no está registrado.
    /// </exception>
    public static ToolApprovalProtector ForChat(HttpContext http, string chatId, string? owner = null, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        var life = lifetime ?? ToolApprovalProtector.DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero, nameof(lifetime));

        var scope = owner ?? EndpointSecurity.GetUserId(http);
        if (string.IsNullOrEmpty(scope))
        {
            throw new InvalidOperationException(
                "Las aprobaciones de herramientas necesitan un dueño: autentique al usuario o indique 'owner' (p. ej. un id de sesión emitido por el servidor).");
        }

        var provider = http.RequestServices.GetService<IDataProtectionProvider>()
                       ?? throw new InvalidOperationException("Las aprobaciones de herramientas usan ASP.NET Core Data Protection: registre services.AddDataProtection().");
        var protector = provider.CreateProtector(Purpose, scope, chatId).ToTimeLimitedDataProtector();
        return new DataProtectionToolApprovalProtector(protector, life);
    }

    private sealed class DataProtectionToolApprovalProtector(ITimeLimitedDataProtector protector, TimeSpan lifetime) : ToolApprovalProtector
    {
        protected override string Protect(ReadOnlySpan<byte> digest) =>
            Base64Url.EncodeToString(protector.Protect(digest.ToArray(), DateTimeOffset.UtcNow + lifetime));

        protected override bool Verify(string approvalId, ReadOnlySpan<byte> digest)
        {
            if (!Base64Url.IsValid(approvalId))
            {
                return false;
            }

            try
            {
                // Falla si el id no es de este dueño y chat (propósito), si se alteró o si expiró.
                var sealedDigest = protector.Unprotect(Base64Url.DecodeFromChars(approvalId), out _);
                return CryptographicOperations.FixedTimeEquals(sealedDigest, digest);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
