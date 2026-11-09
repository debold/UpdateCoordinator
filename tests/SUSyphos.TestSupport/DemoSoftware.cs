using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;

namespace SUSyphos.TestSupport;

/// <summary>Software for the demo and showcase labs: a few catalog packages with versions and the programs of the servers.</summary>
internal static class DemoSoftware
{
    private sealed record Package(string Id, string Name, string Publisher, string[] Versions, bool Agreements = false, string? Url = null);

    private static readonly Package[] Packages =
    [
        new("7zip.7zip", "7-Zip", "Igor Pavlov", ["26.05", "26.06"], Url: "https://www.7-zip.org/"),
        new("Notepad++.Notepad++", "Notepad++", "Notepad++ Team", ["8.9.8", "8.9.9"], Url: "https://notepad-plus-plus.org/"),
        new("Microsoft.PowerShell", "PowerShell", "Microsoft Corporation", ["7.6.0", "7.6.1"], Url: "https://github.com/PowerShell/PowerShell"),
        new("Microsoft.SQLServerManagementStudio", "SQL Server Management Studio", "Microsoft Corporation", ["22.1.0"], Agreements: true),
    ];

    /// <summary>
    /// Adds the packages and the installed programs: every server has 7-Zip (half of them the older version) and a vendor
    /// tool WinGet does not know; <paramref name="admins"/> also have Notepad++ and PowerShell. Does not save.
    /// </summary>
    public static void Add(ServerDbContext db, IReadOnlyCollection<Guid> servers, IReadOnlyCollection<Guid> admins, DateTime now, string addedBy = "inventory")
    {
        foreach (var package in Packages)
        {
            db.Packages.Add(new PackageEntity
            {
                PackageId = package.Id,
                Name = package.Name,
                Publisher = package.Publisher,
                ShortDescription = $"{package.Name} for Windows.",
                License = "See the publisher's licence",
                PackageUrl = package.Url,
                HasAgreements = package.Agreements,
                AddedAtUtc = now.AddDays(-20),
                AddedBy = package.Agreements ? "marc" : addedBy,
                CheckedAtUtc = now.AddHours(-3),
            });
            foreach (var (version, index) in package.Versions.Select((v, i) => (v, i)))
            {
                var updateId = SoftwarePackages.SoftwareUpdateId(package.Id, version);
                db.PackageVersions.Add(new PackageVersionEntity
                {
                    PackageId = package.Id,
                    Version = version,
                    UpdateId = updateId,
                    ReleaseDate = DateOnly.FromDateTime(now.AddDays(-30 + (index * 14))),
                    ScanState = index == 0 ? InstallerScanState.Clean : InstallerScanState.NotScanned,
                    CachedAtUtc = index == 0 ? now.AddDays(-15) : null,
                });
                db.Updates.Add(new UpdateEntity
                {
                    UpdateId = updateId,
                    Revision = SoftwarePackages.SoftwareRevision,
                    Title = SoftwarePackages.Title(package.Name, version),
                    Kind = UpdateKind.Software,
                    PackageId = package.Id,
                    PackageVersion = version,
                    FirstSeenAtUtc = now.AddDays(-30 + (index * 14)),
                });
            }
        }

        var number = 0;
        foreach (var server in servers)
        {
            var zip = number++ % 2 == 0 ? "26.05" : "26.06";
            db.ClientSoftware.Add(Program(server, SoftwareView.Machine64, "7-Zip", $"7-Zip {zip} (x64)", zip, "Igor Pavlov", "7zip.7zip", now));
            db.ClientSoftware.Add(Program(server, SoftwareView.Machine32, "{6A1F5C2B-0B5E-4C3A-9D61-2B7E0A4F9C11}", "Contoso Backup Agent", "5.2.1", "Contoso Ltd.", null, now));
        }

        foreach (var server in admins)
        {
            db.ClientSoftware.Add(Program(server, SoftwareView.Machine64, "Notepad++", "Notepad++ (64-bit x64)", "8.9.8", "Notepad++ Team", "Notepad++.Notepad++", now));
            db.ClientSoftware.Add(Program(server, SoftwareView.Machine64, "{1C3B7A2E-5D0F-4E8A-B6C9-7F2A1D4E8B30}", "PowerShell 7-x64", "7.6.1.0", "Microsoft Corporation", "Microsoft.PowerShell", now, "7.6.1"));
        }
    }

    private static ClientSoftwareEntity Program(Guid server, SoftwareView view, string key, string name, string version, string publisher, string? packageId, DateTime now, string? installed = null) => new()
    {
        ClientId = server,
        View = view,
        KeyName = key,
        DisplayName = name,
        DisplayVersion = version,
        Publisher = publisher,
        PackageId = packageId,
        InstalledVersion = packageId is null ? null : installed ?? version,
        MatchKind = packageId is null ? SoftwareMatch.None : SoftwareMatch.WinGet,
        ReportedAtUtc = now.AddHours(-2),
    };
}
