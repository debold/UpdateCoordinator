using System.Globalization;

using Microsoft.Win32;

namespace SUSyphos.Agent.Actions;

/// <summary>Uninstalls the agent's own package when the server is removed from SUSyphos (Marc, 2026-10-27).</summary>
internal interface IAgentUninstaller
{
    /// <summary>Product code of the installed agent package; null when the agent was not installed with its MSI package.</summary>
    string? FindProductCode();

    /// <summary>Starts the uninstallation and returns at once; msiexec stops this service and outlives it.</summary>
    void Start(string productCode);
}

/// <summary>
/// Finds the agent package through the upgrade code Windows Installer registers (HKLM\SOFTWARE\Microsoft\Windows\
/// CurrentVersion\Installer\UpgradeCodes, codes in Windows Installer's packed form) and runs msiexec /x silently with a
/// log in the agent's data folder. The arguments contain only the product code read there and a path the agent owns.
/// </summary>
internal sealed class MsiAgentUninstaller(string logPath) : IAgentUninstaller
{
    /// <summary>Upgrade code of the agent package (installer/Agent/Package.wxs).</summary>
    public const string UpgradeCode = "F94F20CA-E2D1-4F0C-8D0C-4D810C149471";

    private const string UpgradeCodes = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes";

    public string? FindProductCode()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{UpgradeCodes}\{Pack(Guid.Parse(UpgradeCode))}");
        return key?.GetValueNames().Select(Unpack).FirstOrDefault(code => code is not null) is { } product
            ? product.ToString("B").ToUpperInvariant()
            : null;
    }

    public void Start(string productCode)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/x", productCode, "/qn", "/norestart", "/l*v", logPath })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("msiexec could not be started.");
    }

    /// <summary>
    /// A GUID in the packed form Windows Installer uses in the registry: the first three groups reversed, the rest with
    /// the two characters of every byte swapped.
    /// </summary>
    public static string Pack(Guid guid)
    {
        var hex = guid.ToString("N", CultureInfo.InvariantCulture).ToUpperInvariant();
        var packed = new System.Text.StringBuilder(32);
        packed.Append(Reverse(hex[..8])).Append(Reverse(hex[8..12])).Append(Reverse(hex[12..16]));
        for (var i = 16; i < 32; i += 2)
        {
            packed.Append(hex[i + 1]).Append(hex[i]);
        }

        return packed.ToString();
    }

    /// <summary>The GUID of a packed code; null when the text is not one.</summary>
    public static Guid? Unpack(string? packed)
    {
        if (packed is not { Length: 32 } || !packed.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        // Packing swaps and reverses only, so packing again restores the original order.
        var restored = Pack(Guid.ParseExact(packed, "N"));
        return Guid.ParseExact(restored, "N");
    }

    private static string Reverse(string text) => string.Concat(text.Reverse());
}
