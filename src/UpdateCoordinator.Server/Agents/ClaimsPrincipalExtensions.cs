using System.Globalization;
using System.Security.Claims;

using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Server.Agents;

internal static class ClaimsPrincipalExtensions
{
    public static Guid GetClientId(this ClaimsPrincipal principal) =>
        Guid.ParseExact(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, "D");

    /// <summary>Registration state of an authenticated agent, or null if the caller is not authenticated.</summary>
    public static RegistrationState? GetRegistrationState(this ClaimsPrincipal principal)
    {
        var value = principal.Identity?.IsAuthenticated == true
            ? principal.FindFirstValue(AgentAuthenticationHandler.StateClaim)
            : null;
        return value is null ? null : (RegistrationState)int.Parse(value, CultureInfo.InvariantCulture);
    }
}
