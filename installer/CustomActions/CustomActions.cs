using System.Security.AccessControl;
using System.Security.Cryptography;

using Microsoft.Win32;

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

    /// <summary>
    /// Deferred, elevated: writes the setup token from CustomActionData to HKLM\SOFTWARE\SUSyphos\Server\Setup with an ACL that
    /// lets only SYSTEM and administrators in (protected, no inheritance). A custom action instead of the Registry table,
    /// because Windows Installer writes registry values in clear text to its log, also for hidden properties. A failure is
    /// logged and ignored: the central server then creates its own token in its token file.
    /// </summary>
    [CustomAction]
    public static ActionResult WriteSetupToken(Session session)
    {
        if (session is null)
        {
            return ActionResult.Success;
        }

        try
        {
            var token = session.CustomActionData.ToString();
            if (!IsWellFormedToken(token))
            {
                session.Log("The setup token is malformed (32 to 128 letters, digits, - or _) and was not written.");
                return ActionResult.Success;
            }

            var security = new RegistrySecurity();
            security.SetSecurityDescriptorSddlForm("D:PAI(A;;KA;;;SY)(A;;KA;;;BA)");
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.CreateSubKey(@"SOFTWARE\SUSyphos\Server\Setup", RegistryKeyPermissionCheck.ReadWriteSubTree, security))
            {
                key.SetAccessControl(security);
                key.SetValue("Token", token, RegistryValueKind.String);
            }

            session.Log("Wrote the setup token for the central server.");
        }
        catch (Exception exception) when (exception is InstallerException or UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            session.Log("Writing the setup token failed; the central server creates its own: " + exception.Message);
        }

        return ActionResult.Success;
    }

    /// <summary>The token format the central server accepts (SetupService.IsWellFormed).</summary>
    private static bool IsWellFormedToken(string token)
    {
        if (token is null || token.Length < 32 || token.Length > 128)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
