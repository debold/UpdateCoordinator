namespace SUSyphos.WinGet;

/// <summary>A program as the installed catalog of WinGet lists it.</summary>
/// <param name="Id">WinGet's id: the package id when WinGet correlated it with a source, else a local id such as
/// <c>ARP\Machine\X64\7-Zip</c> or <c>MSIX\…</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="InstalledVersion">Installed version.</param>
/// <param name="AvailableVersion">Newest version in a source, when WinGet correlated it with one.</param>
/// <param name="Source">Name of the source it was correlated with, when it was.</param>
/// <param name="ProductCodes">Product codes WinGet knows for the installed version; for programs from the uninstall
/// keys these are the names of their keys.</param>
/// <param name="InstalledName">Name of the installed program as the uninstall key gives it.</param>
/// <param name="Scope">Installed scope as WinGet reports it (Machine, User), if it knows.</param>
/// <param name="PackageFamilyNames">Package family names, set for packaged apps (MSIX).</param>
public sealed record WinGetInstalledPackage(
    string Id,
    string Name,
    string? InstalledVersion,
    string? AvailableVersion,
    string? Source,
    IReadOnlyList<string> ProductCodes,
    string? InstalledName = null,
    string? Scope = null,
    IReadOnlyList<string>? PackageFamilyNames = null)
{
    /// <summary>True when WinGet found the program in a source, so it could update it.</summary>
    public bool Correlated => Source is not null;

    /// <summary>True for packaged apps (MSIX), which the uninstall keys do not list.</summary>
    public bool Msix => Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase) || PackageFamilyNames is { Count: > 0 };

    /// <summary>True for programs installed for one user, which the machine's uninstall keys do not list.</summary>
    public bool UserScope => string.Equals(Scope, "User", StringComparison.OrdinalIgnoreCase) || Id.StartsWith(@"ARP\User\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How an uninstall entry was matched with a WinGet package.</summary>
public enum MatchKind
{
    /// <summary>WinGet's local id names the uninstall key (<c>ARP\Machine\X64\{key}</c>).</summary>
    LocalId,

    /// <summary>A product code WinGet knows is the name of the uninstall key.</summary>
    ProductCode,

    /// <summary>Same display name (and version where both have one).</summary>
    Name,
}

public sealed record InventoryMatch(UninstallEntry Entry, WinGetInstalledPackage Package, MatchKind Kind);

/// <summary>Result of comparing the uninstall keys with WinGet's installed list.</summary>
/// <param name="Matches">Programs both sides list.</param>
/// <param name="OnlyInRegistry">Programs WinGet does not list.</param>
/// <param name="OnlyInWinGet">Machine-wide programs WinGet lists without a matching uninstall key.</param>
/// <param name="NotCompared">Packaged apps and per-user programs, which the machine's uninstall keys never list.</param>
public sealed record InventoryComparison(
    IReadOnlyList<InventoryMatch> Matches,
    IReadOnlyList<UninstallEntry> OnlyInRegistry,
    IReadOnlyList<WinGetInstalledPackage> OnlyInWinGet,
    IReadOnlyList<WinGetInstalledPackage> NotCompared)
{
    /// <summary>Matched programs WinGet found in a source, i.e. could update.</summary>
    public int Updatable => Matches.Count(match => match.Package.Correlated);
}

/// <summary>
/// Compares the uninstall keys with WinGet's installed list: how much of what is installed WinGet sees and how much of
/// it it could update. Each side is used at most once; stronger keys win (local id, product code, then name).
/// </summary>
public static class InventoryMatcher
{
    public static InventoryComparison Compare(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<WinGetInstalledPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(packages);

        var openEntries = new List<UninstallEntry>(entries);
        var openPackages = packages.Where(package => !package.Msix && !package.UserScope).ToList();
        var matches = new List<InventoryMatch>();

        void Pair(MatchKind kind, Func<UninstallEntry, WinGetInstalledPackage, bool> fits)
        {
            foreach (var entry in openEntries.ToList())
            {
                var package = openPackages.FirstOrDefault(candidate => fits(entry, candidate));
                if (package is not null)
                {
                    matches.Add(new InventoryMatch(entry, package, kind));
                    openEntries.Remove(entry);
                    openPackages.Remove(package);
                }
            }
        }

        Pair(MatchKind.LocalId, (entry, package) => LocalKey(package.Id) is { } key && key.View == entry.View && string.Equals(key.Name, entry.KeyName, StringComparison.OrdinalIgnoreCase));
        Pair(MatchKind.ProductCode, (entry, package) => package.ProductCodes.Any(candidate => string.Equals(candidate.Trim(), entry.KeyName, StringComparison.OrdinalIgnoreCase)));
        Pair(MatchKind.Name, (entry, package) =>
            (string.Equals(entry.DisplayName, package.InstalledName, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.DisplayName, package.Name, StringComparison.OrdinalIgnoreCase))
            && (entry.DisplayVersion is null || package.InstalledVersion is null || string.Equals(entry.DisplayVersion, package.InstalledVersion, StringComparison.OrdinalIgnoreCase)));

        return new InventoryComparison(matches, openEntries, openPackages, packages.Where(package => package.Msix || package.UserScope).ToList());
    }

    /// <summary>The uninstall key a local WinGet id points at (<c>ARP\Machine\X64\{key}</c>), or null.</summary>
    public static (UninstallView View, string Name)? LocalKey(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var parts = id.Split('\\', 4);
        if (parts.Length != 4 || !parts[0].Equals("ARP", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("Machine", StringComparison.OrdinalIgnoreCase) || parts[3].Length == 0)
        {
            return null;
        }

        return parts[2].ToUpperInvariant() switch
        {
            "X64" => (UninstallView.Machine64, parts[3]),
            "X86" => (UninstallView.Machine32, parts[3]),
            _ => null,
        };
    }
}
