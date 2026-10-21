using System.Runtime.Versioning;

using Microsoft.Win32;

namespace SUSyphos.Server.Administration;

/// <summary>
/// The one-time setup token the interactive central server installer creates and shows, so the first administrator does not
/// have to look for the token file. The installer writes it to <c>HKLM\SOFTWARE\SUSyphos\Server\Setup</c>, a key only
/// SYSTEM and local administrators can read (a protected ACL, no inheritance); <c>SETUPTOKEN</c> on the command line sets it
/// for automated installations.
/// </summary>
internal interface IInstallerSetupToken
{
    /// <summary>The token, or null when the installer left none.</summary>
    string? Read();

    /// <summary>Deletes the key, once the setup is completed or no longer possible.</summary>
    void Remove();
}

/// <summary>Reads and removes the installer token in the registry of this machine.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class RegistryInstallerSetupToken(ILogger<RegistryInstallerSetupToken> logger) : IInstallerSetupToken
{
    public const string SubKey = "Setup";
    public const string ValueName = "Token";

    public string? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServerRegistry.KeyPath}\{SubKey}");
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            LogReadFailed(exception.Message);
            return null;
        }
    }

    public void Remove()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServerRegistry.KeyPath, writable: true);
            key?.DeleteSubKeyTree(SubKey, throwOnMissingSubKey: false);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            LogRemoveFailed(exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The setup token of the installer could not be read: {Reason}")]
    private partial void LogReadFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The setup token of the installer could not be removed from the registry: {Reason}")]
    private partial void LogRemoveFailed(string reason);
}
