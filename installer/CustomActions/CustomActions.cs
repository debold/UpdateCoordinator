using System.Security.Cryptography;

using SUSyphos.Contracts.Installation;

using WixToolset.Dtf.WindowsInstaller;

namespace SUSyphos.Installer.CustomActions;

/// <summary>
/// Immediate custom actions of the installers (agent: docs/03-communication-protocol.md, section 3.3; central server: first
/// setup token). They only read and set properties; every failure is logged and ignored, because a file name or a typing
/// error must never break an installation.
/// </summary>
public static class CustomActions
{
    /// <summary>
    /// Reads the central server address and the trust anchor fingerprint from the name of the MSI file
    /// (property OriginalDatabase) into FILENAMESERVERURL and FILENAMEFINGERPRINT.
    /// </summary>
    [CustomAction]
    public static ActionResult ReadInstallerFileName(Session session)
    {
        if (session is null)
        {
            return ActionResult.Success;
        }

        try
        {
            if (AgentInstallerFileName.TryParse(session["OriginalDatabase"], out var url, out var fingerprint))
            {
                session["FILENAMESERVERURL"] = url;
                session["FILENAMEFINGERPRINT"] = fingerprint ?? string.Empty;
                session.Log("Central server address from the installer file name: " + url + (fingerprint is null ? string.Empty : ", trust anchor fingerprint " + fingerprint));
            }
            else
            {
                session.Log("The installer file name carries no central server address.");
            }
        }
        catch (Exception exception) when (exception is InstallerException or ArgumentException or InvalidOperationException)
        {
            session.Log("Reading the installer file name failed: " + exception.Message);
        }

        return ActionResult.Success;
    }

    /// <summary>Checks SERVERURL from the dialog: SERVERURLVALID becomes 1 for an absolute https address, otherwise 0.</summary>
    [CustomAction]
    public static ActionResult ValidateServerUrl(Session session)
    {
        if (session is null)
        {
            return ActionResult.Success;
        }

        try
        {
            var value = (session["SERVERURL"] ?? string.Empty).Trim();
            session["SERVERURL"] = value;
            session["SERVERURLVALID"] = AgentInstallerFileName.IsValidCentralServerUrl(value) ? "1" : "0";
        }
        catch (InstallerException exception)
        {
            session.Log("Checking the central server address failed: " + exception.Message);
            session["SERVERURLVALID"] = "0";
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Creates the one-time token for the first administrator of the central server (SETUPTOKEN, a hidden property) unless one
    /// was given on the command line, and copies it into SETUPTOKENCOPY for the read-only display in the dialog. The central
    /// server takes it over from the protected registry key the installer writes; the value never appears in the log.
    /// </summary>
    [CustomAction]
    public static ActionResult CreateSetupToken(Session session)
    {
        if (session is null)
        {
            return ActionResult.Success;
        }

        try
        {
            var token = session["SETUPTOKEN"];
            if (string.IsNullOrEmpty(token))
            {
                var bytes = new byte[32];
                using (var random = RandomNumberGenerator.Create())
                {
                    random.GetBytes(bytes);
                }

                token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                session["SETUPTOKEN"] = token;
            }

            session["SETUPTOKENCOPY"] = token;
            session.Log("Created the one-time setup token for the first administrator.");
        }
        catch (InstallerException exception)
        {
            session.Log("Creating the setup token failed; the central server creates its own: " + exception.Message);
        }

        return ActionResult.Success;
    }
}
