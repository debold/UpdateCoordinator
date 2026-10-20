using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

using SUSyphos.Common.Logging;

namespace SUSyphos.Server.Tests;

public sealed class StartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailingStart_IsWrittenToTheLog_AndStopsTheProcess()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRollingFileLogging(new ConfigurationBuilder().Build(), _directory, "server");
        var app = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerStartup.RunAsync(app, _ => throw new InvalidOperationException("No TLS certificate found.")));

        Assert.Equal("No TLS certificate found.", error.Message);
        var log = string.Concat(Directory.GetFiles(_directory, "server-*.log").Select(File.ReadAllText));
        Assert.Contains("could not start", log, StringComparison.Ordinal);
        Assert.Contains("No TLS certificate found.", log, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp folder never fails a test.
        }
    }
}
