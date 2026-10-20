using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SUSyphos.Agent.Tests;

public sealed class ResourceGuardTests
{
    private const long Megabyte = 1024 * 1024;

    private readonly FakeProcessResources _process = new();
    private readonly FakeServiceRestarter _restarter = new();

    [Fact]
    public void WithinLimit_DoesNothing()
    {
        _process.PrivateMemoryBytes = 200 * Megabyte;

        Assert.True(Guard().CheckMemory());
        Assert.Equal(0, _process.Collections);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public void AboveLimit_CollectsFirst_AndKeepsRunningWhenThatHelps()
    {
        _process.PrivateMemoryBytes = 250 * Megabyte;
        _process.AfterCollection = 120 * Megabyte;

        Assert.True(Guard().CheckMemory());
        Assert.Equal(1, _process.Collections);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public void StillAboveLimitAfterCollection_RequestsServiceRestart()
    {
        _process.PrivateMemoryBytes = 250 * Megabyte;

        Assert.False(Guard().CheckMemory());
        Assert.Equal(1, _restarter.Requests);
    }

    [Fact]
    public void ConfiguredLimit_IsUsed()
    {
        _process.PrivateMemoryBytes = 250 * Megabyte;

        Assert.True(Guard(new ResourceOptions { MaxPrivateMemoryMegabytes = 300 }).CheckMemory());
    }

    [Fact]
    public void Priority_IsLowered_UnlessSwitchedOff()
    {
        Guard(new ResourceOptions { LowPriority = false }).ApplyPriority();
        Assert.False(_process.PriorityLowered);

        Guard().ApplyPriority();
        Assert.True(_process.PriorityLowered);
    }

    [Fact]
    public void PriorityFailure_DoesNotStopTheAgent()
    {
        _process.PriorityFailure = new Win32Exception(5);

        Guard().ApplyPriority();

        Assert.False(_process.PriorityLowered);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(64, true)]
    [InlineData(4096, true)]
    [InlineData(63, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(4097, false)]
    public void Options_AreValidated(int megabytes, bool valid)
    {
        var options = new ResourceOptions { MaxPrivateMemoryMegabytes = megabytes };

        Assert.Equal(valid, Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true));
    }

    [Fact]
    public void RealProcess_ReportsItsMemory()
    {
        Assert.True(new CurrentProcessResources().PrivateMemoryBytes > 0);
    }

    private ResourceGuard Guard(ResourceOptions? options = null) =>
        new(_process, _restarter, Options.Create(options ?? new ResourceOptions()), NullLogger<ResourceGuard>.Instance);
}
