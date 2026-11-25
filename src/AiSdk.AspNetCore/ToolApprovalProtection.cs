using System.Buffers.Text;
using System.Security.Cryptography;
using AiSdk.UI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.AspNetCore;

/// <summary>
/// Tool approval with <c>useChat</c> (<c>addToolApprovalResponse</c>) in an ASP.NET Core endpoint.
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
    /// approvalId protector for one user's chat, backed by ASP.NET Core Data Protection (keys rotated by the
    /// framework; across multiple instances, share the key ring as you would for cookies). The ids only verify for
    /// the same owner (<paramref name="owner"/> or, if null, the authenticated user) and the same
    /// <paramref name="chatId"/>, and they expire after <paramref name="lifetime"/> (defaults to
    /// <see cref="ToolApprovalProtector.DefaultLifetime"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// If the request has no owner (anonymous and no <paramref name="owner"/>) or Data Protection is not registered.
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
            throw new InvalidOperationException(Resources.Strings.Approval_RequiresOwner);
        }

        var provider = http.RequestServices.GetService<IDataProtectionProvider>()
                       ?? throw new InvalidOperationException(Resources.Strings.Approval_DataProtectionRequired);
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
