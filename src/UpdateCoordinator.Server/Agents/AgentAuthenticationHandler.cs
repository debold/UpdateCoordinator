using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Authenticates agents with HTTP Basic credentials (client GUID and client secret issued at registration).
/// </summary>
internal sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ServerDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Agent";
    public const string StateClaim = "registration_state";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, AgentCredentials.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (!AgentCredentials.TryParse(header.Parameter, out var clientId, out var secret))
        {
            return AuthenticateResult.Fail("Malformed credentials.");
        }

        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.SecretHash, c.State })
            .SingleOrDefaultAsync(Context.RequestAborted)
            .ConfigureAwait(false);

        // Unknown client and wrong secret produce the same result, so client IDs cannot be probed.
        if (client is null || !ClientSecrets.Verify(secret, client.SecretHash))
        {
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, clientId.ToString("D")),
                new Claim(StateClaim, ((int)client.State).ToString(CultureInfo.InvariantCulture)),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
