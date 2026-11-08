namespace SUSyphos.WinGet.Tests;

public sealed class InventoryTests
{
    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FromValues_TakesAVisibleProgram()
    {
        var entry = UninstallInventory.FromValues(UninstallView.Machine64, "7-Zip", Values(("DisplayName", " 7-Zip 24.08 (x64) "), ("DisplayVersion", "24.08"), ("Publisher", "Igor Pavlov"), ("WindowsInstaller", 0)));

        Assert.Equal(new UninstallEntry(UninstallView.Machine64, "7-Zip", "7-Zip 24.08 (x64)", "24.08", "Igor Pavlov", false), entry);
    }

    [Fact]
    public void FromValues_ReadsNumbersStoredAsText()
    {
        var entry = UninstallInventory.FromValues(UninstallView.Machine32, "{A}", Values(("DisplayName", "Tool"), ("WindowsInstaller", "1")));

        Assert.True(entry!.WindowsInstaller);
    }

    [Theory]
    [InlineData("SystemComponent", 1)]
    [InlineData("SystemComponent", "1")]
    [InlineData("ParentKeyName", "OperatingSystem")]
    [InlineData("ReleaseType", "Security Update")]
    [InlineData("ReleaseType", "Hotfix")]
    public void FromValues_SkipsWhatProgramsAndFeaturesHides(string name, object value)
    {
        Assert.Null(UninstallInventory.FromValues(UninstallView.Machine64, "Key", Values(("DisplayName", "Hidden"), (name, value))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(42)]
    public void FromValues_SkipsEntriesWithoutName(object? name)
    {
        Assert.Null(UninstallInventory.FromValues(UninstallView.Machine64, "Key", Values(("DisplayName", name))));
    }

    [Fact]
    public void FromValues_KeepsProgramsWithAnOrdinaryReleaseType()
    {
        Assert.NotNull(UninstallInventory.FromValues(UninstallView.Machine64, "Key", Values(("DisplayName", "Suite"), ("ReleaseType", "Product"), ("SystemComponent", 0))));
    }

    [Theory]
    [InlineData(@"ARP\Machine\X64\7-Zip", UninstallView.Machine64, "7-Zip")]
    [InlineData(@"ARP\Machine\X86\{1D8E6291-B0D5-35EC-8441-6616F567A0F7}", UninstallView.Machine32, "{1D8E6291-B0D5-35EC-8441-6616F567A0F7}")]
    [InlineData(@"arp\machine\x64\Name\With\Backslash", UninstallView.Machine64, @"Name\With\Backslash")]
    public void LocalKey_ReadsTheUninstallKey(string id, UninstallView view, string name)
    {
        Assert.Equal((view, name), InventoryMatcher.LocalKey(id));
    }

    [Theory]
    [InlineData("7zip.7zip")]
    [InlineData(@"ARP\User\X64\Tool")]
    [InlineData(@"ARP\Machine\Arm64\Tool")]
    [InlineData(@"ARP\Machine\X64\")]
    [InlineData(@"MSIX\Microsoft.WindowsTerminal_1.0_x64__8wekyb3d8bbwe")]
    [InlineData("")]
    public void LocalKey_IgnoresOtherIds(string id)
    {
        Assert.Null(InventoryMatcher.LocalKey(id));
    }

    [Fact]
    public void Compare_MatchesByLocalIdThenKeyNameThenName()
    {
        var sevenZip = new UninstallEntry(UninstallView.Machine64, "7-Zip", "7-Zip 24.08 (x64)", "24.08", null, false);
        var redist = new UninstallEntry(UninstallView.Machine32, "{B}", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.32", "14.32", null, false);
        var notepad = new UninstallEntry(UninstallView.Machine64, "Notepad++", "Notepad++ (64-bit x64)", "8.6", null, false);
        var unknown = new UninstallEntry(UninstallView.Machine64, "Tool", "In-house tool", "1.0", null, true);
        var packages = new[]
        {
            new WinGetInstalledPackage(@"ARP\Machine\X64\7-Zip", "7-Zip", "24.08", null, null, []),
            new WinGetInstalledPackage("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.32", "14.44", "winget", ["{b}"]),
            new WinGetInstalledPackage("Notepad++.Notepad++", "Notepad++", "8.6", "8.7", "winget", [], InstalledName: "Notepad++ (64-bit x64)", UpdateAvailable: true),
            new WinGetInstalledPackage(@"ARP\Machine\X64\Gone", "Leftover", "1.0", null, null, []),
        };

        var result = InventoryMatcher.Compare([sevenZip, redist, notepad, unknown], packages);

        Assert.Equal(
            [("7-Zip", MatchKind.LocalId), ("{B}", MatchKind.ProductCode), ("Notepad++", MatchKind.Name)],
            result.Matches.Select(match => (match.Entry.KeyName, match.Kind)));
        Assert.Equal(2, result.Updatable);
        Assert.Equal([unknown], result.OnlyInRegistry);
        Assert.Equal(@"ARP\Machine\X64\Gone", Assert.Single(result.OnlyInWinGet).Id);
        Assert.Empty(result.NotCompared);
    }

    [Fact]
    public void Compare_DoesNotMatchTheSameKeyInTheOtherView()
    {
        var entry = new UninstallEntry(UninstallView.Machine32, "7-Zip", "7-Zip x86", "24.08", null, false);
        var package = new WinGetInstalledPackage(@"ARP\Machine\X64\7-Zip", "7-Zip x64", "24.08", null, null, []);

        var result = InventoryMatcher.Compare([entry], [package]);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Compare_DoesNotMatchANameWithAnotherVersion()
    {
        var entry = new UninstallEntry(UninstallView.Machine64, "Key", "Tool", "1.0", null, false);
        var package = new WinGetInstalledPackage("Vendor.Tool", "Tool", "2.0", "2.1", "winget", []);

        var result = InventoryMatcher.Compare([entry], [package]);

        Assert.Empty(result.Matches);
        Assert.Single(result.OnlyInRegistry);
        Assert.Single(result.OnlyInWinGet);
    }

    [Fact]
    public void Compare_UsesEachPackageOnce()
    {
        var first = new UninstallEntry(UninstallView.Machine64, "A", "Tool", null, null, false);
        var second = new UninstallEntry(UninstallView.Machine32, "B", "Tool", null, null, false);
        var package = new WinGetInstalledPackage("Vendor.Tool", "Tool", "1.0", null, null, []);

        var result = InventoryMatcher.Compare([first, second], [package]);

        Assert.Single(result.Matches);
        Assert.Equal([second], result.OnlyInRegistry);
    }

    [Fact]
    public void Compare_LeavesPackagedAndPerUserProgramsOut()
    {
        var packages = new[]
        {
            new WinGetInstalledPackage(@"MSIX\Microsoft.Paint_11_x64__8wekyb3d8bbwe", "Paint", "11", null, null, []),
            new WinGetInstalledPackage("Microsoft.WindowsTerminal", "Windows Terminal", "1.21", "1.22", "winget", [], PackageFamilyNames: ["Microsoft.WindowsTerminal_8wekyb3d8bbwe"]),
            new WinGetInstalledPackage("Microsoft.VisualStudioCode", "Visual Studio Code", "1.95", "1.96", "winget", [], Scope: "User"),
            new WinGetInstalledPackage(@"ARP\User\X64\Tool", "Tool", "1.0", null, null, []),
        };

        var result = InventoryMatcher.Compare([], packages);

        Assert.Empty(result.OnlyInWinGet);
        Assert.Equal(4, result.NotCompared.Count);
    }

    [Fact]
    public void Compare_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => InventoryMatcher.Compare(null!, []));
        Assert.Throws<ArgumentNullException>(() => InventoryMatcher.Compare([], null!));
    }
}
