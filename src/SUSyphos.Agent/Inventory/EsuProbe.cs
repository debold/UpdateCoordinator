using System.Management;
using System.ServiceProcess;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Inventory;

/// <summary>Finds the signs of Extended Security Updates on the server. Separate so tests decide what the server has.</summary>
internal interface IEsuProbe
{
    ExtendedSecurityUpdatesInfo Read();
}

/// <summary>
/// Reads, without changing anything: the licenses in WMI (SoftwareLicensingProduct with "ESU" in its name and a product
/// key, licensed), the Azure Connected Machine agent service (himds) and the SMBIOS asset tag Azure gives its virtual
/// machines. A source that cannot be read is null, so the central server does not mistake it for "no ESU".
/// </summary>
internal sealed partial class WmiEsuProbe(ILogger<WmiEsuProbe> logger) : IEsuProbe
{
    /// <summary>The chassis asset tag of every Azure virtual machine.</summary>
    internal const string AzureAssetTag = "7783-7084-3265-9085-8269-3286-77";

    private const uint Licensed = 1;

    public ExtendedSecurityUpdatesInfo Read() => new()
    {
        KeyLicensed = Try("ESU license", ReadEsuLicense),
        AzureArc = Try("Azure Arc agent", ReadAzureArc),
        Azure = Try("Azure", ReadAzure),
    };

    private static bool ReadEsuLicense()
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL");
        using var results = searcher.Get();
        var found = false;
        foreach (var item in results)
        {
            using (item)
            {
                if (item["Name"] is string name && name.Contains("ESU", StringComparison.OrdinalIgnoreCase) && item["LicenseStatus"] is uint status && status == Licensed)
                {
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool ReadAzureArc()
    {
        try
        {
            using var service = new ServiceController("himds");
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            // The service does not exist: no Azure Arc agent.
            return false;
        }
    }

    private static bool ReadAzure()
    {
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT SMBIOSAssetTag FROM Win32_SystemEnclosure");
        using var results = searcher.Get();
        var found = false;
        foreach (var item in results)
        {
            using (item)
            {
                found |= string.Equals((item["SMBIOSAssetTag"] as string)?.Trim(), AzureAssetTag, StringComparison.Ordinal);
            }
        }

        return found;
    }

    private bool? Try(string source, Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            LogFailed(source, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the {Source} for Extended Security Updates.")]
    private partial void LogFailed(string source, Exception exception);
}
