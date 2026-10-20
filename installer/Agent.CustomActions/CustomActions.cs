using SUSyphos.Contracts.Installation;

using WixToolset.Dtf.WindowsInstaller;

namespace SUSyphos.Agent.CustomActions;

/// <summary>
/// Immediate custom actions of the agent installer (docs/03-communication-protocol.md, section 3.3). They only read and set
/// properties; every failure is logged and ignored, because a file name or a typing error must never break an installation.
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
}
