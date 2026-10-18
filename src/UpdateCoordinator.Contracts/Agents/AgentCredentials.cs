using System.Net.Http.Headers;
using System.Text;

namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Encodes client credentials as an HTTP Basic authentication header, so agent and server share one format.
/// </summary>
public static class AgentCredentials
{
    public const string Scheme = "Basic";

    public static AuthenticationHeaderValue ToHeader(Guid clientId, string clientSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientSecret);
        var raw = Encoding.UTF8.GetBytes($"{clientId:D}:{clientSecret}");
        return new AuthenticationHeaderValue(Scheme, Convert.ToBase64String(raw));
    }

    /// <summary>Parses the Base64 parameter of a Basic header. Returns false for any malformed input.</summary>
    public static bool TryParse(string? parameter, out Guid clientId, out string clientSecret)
    {
        clientId = Guid.Empty;
        clientSecret = string.Empty;
        if (string.IsNullOrEmpty(parameter) || parameter.Length > 256)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[192];
        if (!Convert.TryFromBase64String(parameter, buffer, out var length))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(buffer[..length]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == decoded.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParseExact(decoded.AsSpan(0, separator), "D", out clientId))
        {
            return false;
        }

        clientSecret = decoded[(separator + 1)..];
        return true;
    }
}
