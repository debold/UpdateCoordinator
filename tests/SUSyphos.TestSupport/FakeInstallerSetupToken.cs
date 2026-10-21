using SUSyphos.Server.Administration;

namespace SUSyphos.TestSupport;

/// <summary>Stands in for the installer's registry key, so tests never read or change HKLM.</summary>
public sealed class FakeInstallerSetupToken : IInstallerSetupToken
{
    /// <summary>The token the "installer" left, or null.</summary>
    public string? Value { get; set; }

    /// <summary>How often the central server removed the key.</summary>
    public int Removed { get; private set; }

    public string? Read() => Value;

    public void Remove()
    {
        Value = null;
        Removed++;
    }
}
