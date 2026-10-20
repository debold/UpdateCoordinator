using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SUSyphos.Agent.Tests;

public sealed class AgentHostTests
{
    [Fact]
    public void Create_RegistersAgentWorker()
    {
        var logs = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));
        var builder = AgentHostBuilder.Create(["--CentralServer:BaseAddress=https://central.example.local/", $"--Logging:File:Directory={logs}"]);

        using var host = builder.Build();

        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is AgentWorker);
    }

    [Fact]
    public void Logging_KeepsHttpRequestsOfEveryHeartbeatOutOfTheLog_ButNotWarnings()
    {
        var logs = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));
        var builder = AgentHostBuilder.Create(["--CentralServer:BaseAddress=https://central.example.local/", $"--Logging:File:Directory={logs}"]);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("System.Net.Http.HttpClient.ICentralServerClient.LogicalHandler");

        Assert.False(logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information));
        Assert.True(logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public void Options_WithHttpsAddress_AreValid()
    {
        Assert.Empty(Validate(new CentralServerOptions { BaseAddress = new Uri("https://central.example.local/") }));
    }

    [Theory]
    [InlineData("http://central.example.local/")]
    [InlineData("ftp://central.example.local/")]
    public void Options_WithoutHttps_AreRejected(string address)
    {
        Assert.NotEmpty(Validate(new CentralServerOptions { BaseAddress = new Uri(address) }));
    }

    [Fact]
    public void Options_WithoutAddress_AreRejected()
    {
        Assert.NotEmpty(Validate(new CentralServerOptions()));
    }

    [Fact]
    public void Options_WithBackoffShorterThanInterval_AreRejected()
    {
        var options = new CentralServerOptions
        {
            BaseAddress = new Uri("https://central.example.local/"),
            HeartbeatInterval = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromSeconds(30),
        };

        Assert.NotEmpty(Validate(options));
    }

    private static List<ValidationResult> Validate(CentralServerOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
