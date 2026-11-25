using System.Net.Security;
using System.Security.Authentication;

namespace SUSyphos.Agent.WinGet;

/// <summary>
/// Before WinGet sees the source, the helper checks it the way WinGet will: one request to its information endpoint with
/// the certificate validation of Windows. WinGet reports an untrusted certificate only as "Rest API internal error"
/// (0x8A15003B); this check names the cause, so the central server can show it.
/// </summary>
internal static class SourceCheck
{
    /// <summary>CERT_E_UNTRUSTEDROOT: reported when Windows does not trust the certificate of the central server.</summary>
    public const int UntrustedCertificate = unchecked((int)0x800B0109);

    /// <summary>
    /// How long the source may take to answer. Right after the central server started it checks the SHA-256 of every
    /// cached installer before it serves the source (lab, 2026-11-25: more than 30 seconds with WebView2 in the cache).
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>ERROR_INTERNET_TIMEOUT: the source did not answer in time.</summary>
    public const int TimedOut = unchecked((int)0x80072EE2);

    /// <summary>Null when the source answers over a trusted connection; else the outcome to report.</summary>
    public static async Task<(int HResult, string Detail)?> CheckAsync(Uri source, Func<HttpMessageHandler>? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? subject = null;
        var errors = SslPolicyErrors.None;
        var handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Windows decides, as for WinGet; the callback only remembers what it said.
                RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
                {
                    subject = certificate?.Subject;
                    errors = policyErrors;
                    return policyErrors == SslPolicyErrors.None;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        try
        {
            using var response = await client.GetAsync(new Uri(source, "information")).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? null
                : (unchecked((int)0x8A15003B), $"The WinGet source of the central server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (HttpRequestException exception) when (exception.InnerException is AuthenticationException || errors != SslPolicyErrors.None)
        {
            return (UntrustedCertificate, Untrusted(subject, errors));
        }
        catch (TaskCanceledException)
        {
            return (TimedOut, $"The WinGet source of the central server did not answer within {Timeout.TotalMinutes:0} minutes; the central server may still be starting. The next installation run tries again.");
        }
        catch (HttpRequestException exception)
        {
            return (exception.HResult, $"The WinGet source of the central server cannot be reached: {exception.Message}");
        }
    }

    internal static string Untrusted(string? subject, SslPolicyErrors errors) =>
        $"This server does not trust the certificate of the central server{(subject is null ? string.Empty : $" ({subject})")}{(errors == SslPolicyErrors.RemoteCertificateNameMismatch ? ": its name does not match the address" : string.Empty)}. Use a certificate of a CA the servers trust, or switch on the agent setting \"Trust the central server's own certificate for WinGet\".";
}
