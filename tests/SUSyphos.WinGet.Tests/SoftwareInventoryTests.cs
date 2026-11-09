using SUSyphos.Agent.WinGet;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.WinGet.Tests;

/// <summary>The software inventory the WinGet helper of the agent builds and how it fits it into the contract.</summary>
public sealed class SoftwareInventoryTests
{
    private static readonly UninstallEntry SevenZip = new(UninstallView.Machine64, "7-Zip", "7-Zip 26.06 (x64)", "26.06", "Igor Pavlov", false);
    private static readonly UninstallEntry Vendor = new(UninstallView.Machine32, "{11111111-2222-3333-4444-555555555555}", "Vendor Agent", "5.1", "Vendor", true);

    [Theory]
    [InlineData(14393, "Server", WinGetAvailability.TooOld)]
    [InlineData(17763, "Server", WinGetAvailability.Available)]
    [InlineData(26100, "Server Core", WinGetAvailability.ServerCore)]
    [InlineData(20348, " server core ", WinGetAvailability.ServerCore)]
    [InlineData(26100, "Client", WinGetAvailability.Available)]
    [InlineData(26100, null, WinGetAvailability.Available)]
    [InlineData(14393, "Server Core", WinGetAvailability.TooOld)]
    public void Availability_FollowsBuildAndInstallationType(int build, string? installationType, WinGetAvailability expected)
    {
        Assert.Equal(expected, SoftwareInventory.Availability(build, installationType));
    }

    [Fact]
    public void Build_KeepsEveryProgram_AndTakesThePackageOnlyWhereWinGetFoundItInASource()
    {
        WinGetInstalledPackage[] packages =
        [
            new(@"7zip.7zip", "7-Zip", "26.06", "26.07", "winget", ["7-Zip"]),
            new(@"ARP\Machine\X86\{11111111-2222-3333-4444-555555555555}", "Vendor Agent", "5.1", null, null, []),
            new(@"Microsoft.WindowsTerminal", "Windows Terminal", "1.22.1", "1.22.1", "winget", [], Scope: "Machine", PackageFamilyNames: ["Microsoft.WindowsTerminal_8wekyb3d8bbwe"]),
            new(@"MSIX\Some.App_1.0_x64__abc", "Some App", "1.0", null, null, [], PackageFamilyNames: ["Some.App_abc"]),
            new(@"ARP\User\X64\Tool", "User Tool", "2.0", null, null, [], Scope: "User"),
            new(@"Foo.UserMsix", "User Msix", "3.0", null, "winget", [], Scope: "User", PackageFamilyNames: ["Foo.UserMsix_x"]),
        ];

        var items = SoftwareInventory.Build([SevenZip, Vendor], packages);

        Assert.Equal(4, items.Count);
        var zip = items.Single(i => i.KeyName == "7-Zip");
        Assert.Equal(("7zip.7zip", "26.06"), (zip.PackageId, zip.InstalledVersion));
        var vendor = items.Single(i => i.Source == SoftwareSource.Machine32);
        Assert.Null(vendor.PackageId);
        Assert.Null(vendor.InstalledVersion);
        var terminal = items.Single(i => i.KeyName == "Microsoft.WindowsTerminal_8wekyb3d8bbwe");
        Assert.Equal((SoftwareSource.Msix, "Microsoft.WindowsTerminal"), (terminal.Source, terminal.PackageId));
        var app = items.Single(i => i.KeyName == "Some.App_abc");
        Assert.Null(app.PackageId);
        Assert.DoesNotContain(items, i => i.DisplayName is "User Tool" or "User Msix");
    }

    [Fact]
    public void FromRegistry_HasNoPackages()
    {
        var items = SoftwareInventory.FromRegistry([SevenZip, Vendor]);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.PackageId));
        Assert.Equal(SoftwareSource.Machine32, items[1].Source);
    }

    [Fact]
    public void Contract_TakesValidValuesAsTheyAre()
    {
        var entry = InventoryCommand.ToContract(new SoftwareItem(SoftwareSource.Machine32, "{K}", "Name", "1.2.3-beta+4", "Publisher", "Vendor.Tool", "1.2.3"));

        Assert.NotNull(entry);
        Assert.Equal(SoftwareView.Machine32, entry.View);
        Assert.Equal(("Vendor.Tool", "1.2.3", "1.2.3-beta+4"), (entry.PackageId, entry.InstalledVersion, entry.DisplayVersion));
    }

    [Fact]
    public void Contract_CleansOddValues_InsteadOfFailingTheInventory()
    {
        var entry = InventoryCommand.ToContract(new SoftwareItem(
            SoftwareSource.Machine64,
            "  Key\u0000  ",
            "Name\twith\ttabs" + new string('x', 300),
            "1.0\r\n",
            "  ",
            "not a package id",
            "1.0"));

        Assert.NotNull(entry);
        Assert.Equal("Key", entry.KeyName);
        Assert.Equal(256, entry.DisplayName.Length);
        Assert.DoesNotContain('\t', entry.DisplayName);
        Assert.Equal("1.0", entry.DisplayVersion);
        Assert.Null(entry.Publisher);
        Assert.Null(entry.PackageId);
        Assert.Null(entry.InstalledVersion);
    }

    [Theory]
    [InlineData("<script>", null)]
    [InlineData("1.0;rm", null)]
    [InlineData(" 1.0 ", "1.0")]
    [InlineData("2.4 (build 7)", "2.4 (build 7)")]
    public void Contract_KeepsOnlyVersionsInThePattern(string version, string? expected)
    {
        var entry = InventoryCommand.ToContract(new SoftwareItem(SoftwareSource.Machine64, "K", "N", version, null, null, null));

        Assert.Equal(expected, entry!.DisplayVersion);
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("Key", "\u0001\u0002")]
    public void Contract_SkipsEntriesWithoutKeyOrName(string key, string name)
    {
        Assert.Null(InventoryCommand.ToContract(new SoftwareItem(SoftwareSource.Machine64, key, name, null, null, null, null)));
    }

    [Fact]
    public void Report_IsLimited_AndCarriesTheReason()
    {
        var items = Enumerable.Range(0, InstalledSoftware.MaxEntries + 10).Select(i => new SoftwareItem(SoftwareSource.Machine64, $"K{i}", $"N{i}", null, null, null, null)).ToList();

        var report = InventoryCommand.Report(items, SoftwareSupportState.NotSupported, InventoryCommand.NotSupportedReason(WinGetAvailability.ServerCore));

        Assert.Equal(InstalledSoftware.MaxEntries, report.Software.Count);
        Assert.Equal(SoftwareSupportState.NotSupported, report.Support.State);
        Assert.Contains("Server Core", report.Support.Detail, StringComparison.Ordinal);
        Assert.Contains("2019", InventoryCommand.NotSupportedReason(WinGetAvailability.TooOld), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_RoundTripsThroughJson()
    {
        var report = InventoryCommand.Report([new SoftwareItem(SoftwareSource.Msix, "Pfn_x", "App", "1.0", null, "Vendor.App", "1.0")], SoftwareSupportState.Supported, null);

        var json = System.Text.Json.JsonSerializer.Serialize(report, SoftwareInventoryReport.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<SoftwareInventoryReport>(json, SoftwareInventoryReport.JsonOptions)!;

        Assert.Contains("\"Msix\"", json, StringComparison.Ordinal);
        Assert.Equal(report.Software[0], back.Software[0]);
        Assert.Equal(SoftwareSupportState.Supported, back.Support.State);
    }

    [Fact]
    public void InstallArguments_AreTakenWhenValid()
    {
        var parsed = InstallCommand.Parse(["install", "7zip.7zip", "26.06", "https://central:8443/winget/api/", "accept-agreements"], out var error);

        Assert.Null(error);
        Assert.Equal(("7zip.7zip", "26.06", new Uri("https://central:8443/winget/api/"), true), parsed);
        Assert.False(InstallCommand.Parse(["install", "7zip.7zip", "26.06", "https://central:8443/winget/api/"], out _)!.Value.AcceptAgreements);
    }

    [Theory]
    [InlineData("install", "7zip 7zip", "26.06", "https://central/winget/api/", null)]
    [InlineData("install", "7zip.7zip", "26.06 & calc", "https://central/winget/api/", null)]
    [InlineData("install", "7zip.7zip", "26.06", "http://central/winget/api/", null)]
    [InlineData("install", "7zip.7zip", "26.06", "https://user:pw@central/winget/api/", null)]
    [InlineData("install", "7zip.7zip", "26.06", "https://central/winget/api/?x=1", null)]
    [InlineData("install", "7zip.7zip", "26.06", "https://central/winget/api/", "--override")]
    [InlineData("install", "7zip.7zip", "26.06", null, null)]
    public void InstallArguments_AreRefusedWhenInvalid(string command, string id, string version, string? source, string? extra)
    {
        List<string> arguments = [command, id, version];
        if (source is not null)
        {
            arguments.Add(source);
        }

        if (extra is not null)
        {
            arguments.Add(extra);
        }

        Assert.Null(InstallCommand.Parse(arguments, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(new[] { "uninstall", "7zip.7zip" }, true)]
    [InlineData(new[] { "uninstall", "7zip 7zip" }, false)]
    [InlineData(new[] { "uninstall", "7zip.7zip", "--force" }, false)]
    [InlineData(new[] { "uninstall" }, false)]
    public void UninstallArguments_AreChecked(string[] arguments, bool valid)
    {
        Assert.Equal(valid, UninstallCommand.Parse(arguments, out _) is not null);
    }}
