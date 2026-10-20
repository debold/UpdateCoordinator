using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SUSyphos.Common.Logging;

namespace SUSyphos.Common.Tests;

public sealed class FileLoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Messages_AreWrittenToDailyFileWithPrefix()
    {
        WriteLog(new(), "hello file log");

        var file = Assert.Single(Directory.GetFiles(_directory, "test-*.log"));
        Assert.Contains("hello file log", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("[INF]", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredDirectory_OverridesDefault()
    {
        var custom = Path.Combine(_directory, "custom");

        WriteLog(new() { ["Logging:File:Directory"] = custom }, "custom folder");

        Assert.Single(Directory.GetFiles(custom, "test-*.log"));
    }

    [Fact]
    public void MessagesBelowMinimumLevel_AreNotWritten()
    {
        WriteLog(new() { ["Logging:File:MinimumLevel"] = "Warning" }, "information is filtered");

        var files = Directory.Exists(_directory) ? Directory.GetFiles(_directory) : [];
        Assert.All(files, file => Assert.DoesNotContain("information is filtered", File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Logging:File:RetainedFileCountLimit", "0")]
    [InlineData("Logging:File:RetainedFileCountLimit", "100000")]
    [InlineData("Logging:File:FileSizeLimitBytes", "10")]
    public void InvalidSettings_AreRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(key, value)]).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRollingFileLogging(configuration, _directory, "test"));
    }

    [Theory]
    [InlineData("Logging:File:MinimumLevel", "Loud")]
    [InlineData("Logging:File:RetainedFileCountLimit", "many")]
    public void UnparsableSettings_AreRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(key, value)]).Build();

        Assert.ThrowsAny<InvalidOperationException>(() => new ServiceCollection().AddRollingFileLogging(configuration, _directory, "test"));
    }

    private void WriteLog(Dictionary<string, string?> settings, string message)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddRollingFileLogging(configuration, _directory, "test");
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogInformation("{Message}", message);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
