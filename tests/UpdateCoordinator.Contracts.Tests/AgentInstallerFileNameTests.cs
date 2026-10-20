using UpdateCoordinator.Contracts.Installation;
using UpdateCoordinator.Contracts.Trust;

namespace UpdateCoordinator.Contracts.Tests;

public sealed class AgentInstallerFileNameTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Create_ThenParse_RoundTrips()
    {
        var name = AgentInstallerFileName.Create("0.3.0", new Uri("https://UC01.lab.local:8443/"), Fingerprint);

        Assert.Equal($"UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443_{Fingerprint}.msi", name);
        Assert.True(AgentInstallerFileName.TryParse(name, out var url, out var fingerprint));
        Assert.Equal("https://uc01.lab.local:8443/", url);
        Assert.Equal(Fingerprint, fingerprint);
    }

    [Theory]
    [InlineData(@"C:\Users\marc\Downloads\UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443_0123456789abcdef0123456789abcdef (1).msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443_0123456789ABCDEF0123456789ABCDEF(2).msi")]
    [InlineData(@"\\fileserver\share\UpdateCoordinator.Agent-0.3.0-rc.1_uc01.lab.local_8443_0123456789abcdef0123456789abcdef.MSI")]
    public void Parse_ToleratesPathsSuffixesAndCase(string path)
    {
        Assert.True(AgentInstallerFileName.TryParse(path, out var url, out var fingerprint));
        Assert.Equal("https://uc01.lab.local:8443/", url);
        Assert.Equal(Fingerprint, fingerprint);
    }

    [Fact]
    public void Parse_WithoutFingerprintOrVersion_GivesAddressOnly()
    {
        Assert.True(AgentInstallerFileName.TryParse("UpdateCoordinator.Agent_10.0.0.5_443.msi", out var url, out var fingerprint));
        Assert.Equal("https://10.0.0.5:443/", url);
        Assert.Null(fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UpdateCoordinator.Agent-0.3.0.msi")]
    [InlineData("UpdateCoordinator.Agent.msi")]
    [InlineData("setup.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.lab.local_0.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.lab.local_65536.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443_0123.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01..local_8443.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_-uc01.local_8443.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.local._8443.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_evil.com@uc01_8443.msi")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443.msi.exe")]
    [InlineData("UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443_0123456789abcdef0123456789abcdeg.msi")]
    public void Parse_RejectsEverythingElse(string? path)
    {
        Assert.False(AgentInstallerFileName.TryParse(path, out var url, out var fingerprint));
        Assert.Null(url);
        Assert.Null(fingerprint);
    }

    [Fact]
    public void Parse_RejectsOverlongInput()
    {
        Assert.False(AgentInstallerFileName.TryParse("UpdateCoordinator.Agent_" + new string('a', 2000) + "_443.msi", out _, out _));
    }

    [Theory]
    [InlineData("http://uc01.lab.local:8080/")]
    [InlineData("https://uc01.lab.local/sub/")]
    [InlineData("https://[::1]:8443/")]
    [InlineData("https://uc01.lab.local/?a=b")]
    public void Create_RejectsAddressesAFileNameCannotCarry(string url)
    {
        Assert.Null(AgentInstallerFileName.Create("0.3.0", new Uri(url), Fingerprint));
    }

    [Theory]
    [InlineData("0.3.0", "abc")]
    [InlineData("0.3.0", "0123456789abcdef0123456789abcdeZ")]
    [InlineData("0.3.0/../x", Fingerprint)]
    public void Create_RejectsInvalidParts(string version, string fingerprint)
    {
        Assert.Null(AgentInstallerFileName.Create(version, new Uri("https://uc01:8443/"), fingerprint));
    }

    [Theory]
    [InlineData("https://uc01.lab.local:8443/")]
    [InlineData("https://uc01/")]
    [InlineData("https://10.0.0.5/")]
    [InlineData("https://[fd00::5]:8443/")]
    public void CentralServerUrl_AcceptsHttpsAddresses(string url)
    {
        Assert.True(AgentInstallerFileName.IsValidCentralServerUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("uc01.lab.local")]
    [InlineData("http://uc01.lab.local/")]
    [InlineData("ftp://uc01/")]
    [InlineData("https://user:secret@uc01/")]
    [InlineData("https://uc_01/")]
    [InlineData("https:///path")]
    [InlineData("javascript:alert(1)")]
    public void CentralServerUrl_RejectsEverythingElse(string? url)
    {
        Assert.False(AgentInstallerFileName.IsValidCentralServerUrl(url));
    }

    [Fact]
    public void CentralServerUrl_RejectsOverlongAddresses()
    {
        Assert.False(AgentInstallerFileName.IsValidCentralServerUrl("https://uc01/" + new string('a', AgentInstallerFileName.MaxUrlLength)));
    }

    [Fact]
    public void AnchorFingerprint_IsShortLowercaseHex()
    {
        var fingerprint = TrustBindingFormat.ComputeAnchorFingerprint([1, 2, 3]);

        Assert.Equal(AgentInstallerFileName.FingerprintLength, fingerprint.Length);
        Assert.Equal(fingerprint.ToLowerInvariant(), fingerprint);
    }
}
