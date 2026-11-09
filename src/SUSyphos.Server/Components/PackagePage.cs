namespace SUSyphos.Server.Components;

/// <summary>Addresses of the package page.</summary>
public static class PackagePage
{
    /// <summary>The page of a package; the ID is escaped (WinGet IDs may hold "+").</summary>
    public static string Link(string packageId) => "/software/" + Uri.EscapeDataString(packageId);
}
