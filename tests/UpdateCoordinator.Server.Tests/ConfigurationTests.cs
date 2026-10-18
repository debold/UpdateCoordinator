using Microsoft.Extensions.Options;

using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

/// <summary>
/// Invalid configuration must stop the central server at startup instead of running with wrong settings.
/// </summary>
public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("Trust:CertificateThumbprint", "not-a-thumbprint")]
    [InlineData("Trust:CertificateThumbprint", "ABCDEF")]
    [InlineData("Agents:RateLimit:RegisterPermitLimit", "0")]
    [InlineData("Agents:RateLimit:RegisterPermitLimit", "-1")]
    [InlineData("Agents:RateLimit:Window", "00:00:00")]
    [InlineData("Database:Path", "")]
    public async Task InvalidSetting_PreventsStartup(string key, string value)
    {
        await using var factory = new ServerFactory();
        await using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting(key, value));

        var exception = Record.Exception(() => configured.CreateClient());

        Assert.NotNull(exception);
        Assert.True(exception is OptionsValidationException or InvalidOperationException, $"Unexpected exception {exception.GetType()}");
    }

    [Theory]
    [InlineData("Agents:RateLimit:RegisterPermitLimit", "many")]
    [InlineData("Agents:RateLimit:Window", "soon")]
    public async Task UnparsableSetting_PreventsStartup(string key, string value)
    {
        await using var factory = new ServerFactory();
        await using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting(key, value));

        Assert.ThrowsAny<InvalidOperationException>(() => configured.CreateClient());
    }
}
