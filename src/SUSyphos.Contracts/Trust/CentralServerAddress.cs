namespace SUSyphos.Contracts.Trust;

/// <summary>
/// What a central server address may look like, shared by the central server (announcing a move) and the agent
/// (accepting one), so both normalize and compare addresses the same way (docs/03-communication-protocol.md, 3.4).
/// </summary>
public static class CentralServerAddress
{
    /// <summary>Upper bound for an address.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Accepts an absolute HTTPS address without user information, query or fragment and returns it with a trailing slash,
    /// e.g. "https://updates.example.local:8443/".
    /// </summary>
    public static bool TryNormalize(string? text, out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > MaxLength
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
        {
            return false;
        }

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        address = new UriBuilder(Uri.UriSchemeHttps, uri.IdnHost, uri.Port, path).Uri;
        return true;
    }

    /// <summary>True if both are the same central server address after normalizing.</summary>
    public static bool AreSame(Uri? first, Uri? second) =>
        TryNormalize(first?.OriginalString, out var a) && TryNormalize(second?.OriginalString, out var b) && a == b;
}
