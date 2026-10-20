using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace SUSyphos.Server.Tests;

/// <summary>Uses a throw-away key under HKEY_CURRENT_USER so the tests need no administrator rights.</summary>
public sealed class ServerRegistryTests : IDisposable
{
    private readonly string _prefix = $@"Software\SUSyphos.Tests\{Guid.NewGuid():N}\";

    [Fact]
    public void InstallerValues_OverrideJsonButNotEnvironment()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_prefix + ServerRegistry.KeyPath))
        {
            key.SetValue("CertificateThumbprint", "ABCDEF0123", RegistryValueKind.String);
            key.SetValue("HttpsUrl", "https://*:9443", RegistryValueKind.String);
            key.SetValue("Unrelated", "ignored", RegistryValueKind.String);
        }

        var builder = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream("{\"Kestrel\":{\"Endpoints\":{\"Https\":{\"Url\":\"https://*:8443\"}}}}"u8.ToArray()))
            .AddInMemoryCollection([new("Trust:CertificateThumbprint", "FROM-ENVIRONMENT")]);
        builder.AddServerRegistry(Registry.CurrentUser, _prefix);
        var configuration = builder.Build();

        Assert.Equal("https://*:9443", configuration["Kestrel:Endpoints:Https:Url"]);
        Assert.Equal("FROM-ENVIRONMENT", configuration["Trust:CertificateThumbprint"]);
        Assert.Null(configuration["Unrelated"]);
    }

    [Fact]
    public void NonStringOrMissingValues_AreIgnored()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_prefix + ServerRegistry.KeyPath))
        {
            key.SetValue("HttpsUrl", 9443, RegistryValueKind.DWord);
        }

        var configuration = new ConfigurationBuilder().AddServerRegistry(Registry.CurrentUser, _prefix).Build();

        Assert.Null(configuration["Kestrel:Endpoints:Https:Url"]);
        Assert.Null(configuration["Trust:CertificateThumbprint"]);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_prefix.TrimEnd('\\'), throwOnMissingSubKey: false);
}
