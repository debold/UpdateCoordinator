using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace UpdateCoordinator.Agent.Tests;

public sealed class AgentHostTests
{
    [Fact]
    public void Create_RegistersAgentWorker()
    {
        var builder = AgentHostBuilder.Create(["--CentralServer:BaseAddress=https://central.example.local/"]);

        using var host = builder.Build();

        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is AgentWorker);
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
