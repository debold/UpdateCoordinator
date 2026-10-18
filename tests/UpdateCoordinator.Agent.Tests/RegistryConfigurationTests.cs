using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

using UpdateCoordinator.Agent.Configuration;

namespace UpdateCoordinator.Agent.Tests;

/// <summary>
/// Uses a throw-away key under HKEY_CURRENT_USER so the tests need no administrator rights.
/// </summary>
public sealed class RegistryConfigurationTests : IDisposable
{
    private const string AddressKey = "CentralServer:BaseAddress";
    private readonly string _prefix = $@"Software\UpdateCoordinator.Tests\{Guid.NewGuid():N}\";

    [Fact]
    public void InstallerValue_IsUsed()
    {
        Write(AgentRegistry.InstallerKeyPath, "CentralServerUrl", "https://installer.example.local/");

        Assert.Equal("https://installer.example.local/", Build()[AddressKey]);
    }

    [Fact]
    public void PolicyValue_OverridesInstallerAndOtherSources()
    {
        Write(AgentRegistry.InstallerKeyPath, "CentralServerUrl", "https://installer.example.local/");
        Write(AgentRegistry.PolicyKeyPath, "CentralServerUrl", "https://policy.example.local/");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new(AddressKey, "https://appsettings.example.local/")])
            .AddAgentRegistry(Registry.CurrentUser, _prefix)
            .Build();

        Assert.Equal("https://policy.example.local/", configuration[AddressKey]);
    }

    [Fact]
    public void MissingKeys_ProvideNoValue()
    {
        Assert.Null(Build()[AddressKey]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValue_IsIgnored(string value)
    {
        Write(AgentRegistry.PolicyKeyPath, "CentralServerUrl", value);

        Assert.Null(Build()[AddressKey]);
    }

    [Fact]
    public void NonStringValue_IsIgnored()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_prefix + AgentRegistry.PolicyKeyPath))
        {
            key.SetValue("CentralServerUrl", 42, RegistryValueKind.DWord);
        }

        Assert.Null(Build()[AddressKey]);
    }

    [Fact]
    public void OverlongValue_IsIgnored()
    {
        Write(AgentRegistry.PolicyKeyPath, "CentralServerUrl", "https://" + new string('a', 3_000));

        Assert.Null(Build()[AddressKey]);
    }

    [Fact]
    public void UnknownValues_AreNotMapped()
    {
        Write(AgentRegistry.PolicyKeyPath, "Logging:LogLevel:Default", "Trace");

        Assert.Null(Build()["Logging:LogLevel:Default"]);
    }

    [Fact]
    public void Value_IsTrimmed()
    {
        Write(AgentRegistry.InstallerKeyPath, "CentralServerUrl", "  https://installer.example.local/  ");

        Assert.Equal("https://installer.example.local/", Build()[AddressKey]);
    }

    [Theory]
    [InlineData("http://central.example.local/")]
    [InlineData("ftp://central.example.local/")]
    [InlineData("central.example.local")]
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("\\\\server\\share")]
    public void InvalidAddressInRegistry_StopsStartupWithClearError(string value)
    {
        Write(AgentRegistry.PolicyKeyPath, "CentralServerUrl", value);
        var configuration = Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddAgent(configuration)
            .BuildServiceProvider();

        var exception = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IOptions<CentralServerOptions>>().Value);

        Assert.True(
            exception is OptionsValidationException or InvalidOperationException,
            $"Unexpected exception type {exception.GetType()}");
        Assert.Contains("BaseAddress", exception.Message + exception.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidAddressInRegistry_IsUsedByAgent()
    {
        Write(AgentRegistry.PolicyKeyPath, "CentralServerUrl", "https://central.example.local:8443/");
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddAgent(Build())
            .BuildServiceProvider();

        Assert.Equal(new Uri("https://central.example.local:8443/"), provider.GetRequiredService<IOptions<CentralServerOptions>>().Value.BaseAddress);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_prefix.TrimEnd('\\'), throwOnMissingSubKey: false);

        // Remove the shared parent key once the last test class instance is done with it.
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\UpdateCoordinator.Tests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\UpdateCoordinator.Tests", throwOnMissingSubKey: false);
        }
    }

    private IConfigurationRoot Build() => new ConfigurationBuilder().AddAgentRegistry(Registry.CurrentUser, _prefix).Build();

    private void Write(string keyPath, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_prefix + keyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }
}
