using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SUSyphos.TestSupport;

/// <summary>
/// Stands in for the Negotiate (Windows) scheme in tests: the real handler needs Kestrel and a domain. The header
/// <see cref="Header"/> carries "SID|DOMAIN\name|group SID,group SID"; without it the request is not authenticated, and
/// a challenge answers 401 with "WWW-Authenticate: Negotiate" like the real handler.
/// </summary>
public sealed class FakeWindowsAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Test-Windows";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || value.ToString().Split('|') is not [var sid, var name, var groups])
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new(ClaimTypes.Name, name) };
        claims.AddRange(groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim(ClaimTypes.GroupSid, g)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Negotiate")), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Negotiate";
        return Task.CompletedTask;
    }
}
