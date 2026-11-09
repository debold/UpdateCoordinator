using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent.Inventory;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

/// <summary>The agent side of the software inventory: running the WinGet helper and checking what it wrote.</summary>
public sealed class SoftwareInventoryTests
{
    private static InstalledSoftware Program(string key = "7-Zip") => new()
    {
        View = SoftwareView.Machine64,
        KeyName = key,
        DisplayName = "7-Zip 26.06 (x64)",
        DisplayVersion = "26.06",
        Publisher = "Igor Pavlov",
        PackageId = "7zip.7zip",
        InstalledVersion = "26.06",
    };

    private static string Json(SoftwareInventoryReport report) => JsonSerializer.Serialize(report, SoftwareInventoryReport.JsonOptions);

    private static WinGetHelperInventory Source(FakeRunner runner) => new(runner, NullLogger<WinGetHelperInventory>.Instance);

    [Fact]
    public async Task HelperReport_IsPassedOn()
    {
        var runner = new FakeRunner { Result = (0, Json(new SoftwareInventoryReport { Software = [Program()], Support = new SoftwareSupport { State = SoftwareSupportState.Supported } })) };

        var result = await Source(runner).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("inventory", runner.Command);
        Assert.Equal(WinGetHelperInventory.Timeout, runner.Timeout);
        Assert.Equal("7zip.7zip", Assert.Single(result.Software!).PackageId);
        Assert.Equal(SoftwareSupportState.Supported, result.Support.State);
    }

    [Fact]
    public async Task EntriesTheServerWouldRefuse_AreDropped_TheRestIsKept()
    {
        // Written by hand: the helper should never write such entries, but one must not cost the whole inventory.
        const string Output = """
            {"software":[
              {"view":"Machine64","keyName":"Good","displayName":"Good"},
              {"view":"Machine64","keyName":"Bad","displayName":"Bad","packageId":"no-dots"},
              {"view":"Machine32","keyName":"Ctrl","displayName":"Bad\u0007Name"}],
             "support":{"state":"NotSupported","detail":"WinGet needs Windows Server 2019 or later."}}
            """;
        var result = await Source(new FakeRunner { Result = (0, Output) }).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Good", Assert.Single(result.Software!).KeyName);
        Assert.Equal(SoftwareSupportState.NotSupported, result.Support.State);
    }

    [Fact]
    public async Task InvalidSupportState_BecomesFailed()
    {
        var output = Json(new SoftwareInventoryReport { Software = [Program()], Support = new SoftwareSupport { State = SoftwareSupportState.Failed, Detail = "bad\nline" } });

        var result = await Source(new FakeRunner { Result = (0, output) }).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Single(result.Software!);
        Assert.Equal(SoftwareSupportState.Failed, result.Support.State);
        Assert.DoesNotContain('\n', result.Support.Detail!);
    }

    [Fact]
    public async Task TooManyEntries_AreCut()
    {
        var output = Json(new SoftwareInventoryReport
        {
            Software = [.. Enumerable.Range(0, InstalledSoftware.MaxEntries + 5).Select(i => Program($"K{i}"))],
            Support = new SoftwareSupport { State = SoftwareSupportState.Supported },
        });

        var result = await Source(new FakeRunner { Result = (0, output) }).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstalledSoftware.MaxEntries, result.Software!.Count);
    }

    public static readonly TheoryData<int, string?, string> Failures = new()
    {
        { 0, null, "did not finish in time" },
        { 1, "", "failed" },
        { 0, "not json", "no valid inventory" },
        { 0, "null", "no valid inventory" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task HelperFailing_ReportsNoProgramsAndTheReason(int exitCode, string? output, string reason)
    {
        var runner = new FakeRunner { Result = output is null ? null : (exitCode, output) };

        var result = await Source(runner).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Software);
        Assert.Equal(SoftwareSupportState.Failed, result.Support.State);
        Assert.Contains(reason, result.Support.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(Win32Exception))]
    public async Task HelperNotStarting_ReportsNoPrograms(Type exception)
    {
        var runner = new FakeRunner { Throw = (Exception)Activator.CreateInstance(exception)! };

        var result = await Source(runner).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Software);
        Assert.Contains("could not be started", result.Support.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_CarriesTheSoftware_AndWithoutASourceNone()
    {
        var probe = new FakeSystemProbe();
        var roles = new RoleDetector(probe, Options.Create(new InventoryOptions()));
        var runner = new FakeRunner { Result = (0, Json(new SoftwareInventoryReport { Software = [Program()], Support = new SoftwareSupport { State = SoftwareSupportState.Supported } })) };

        var with = await new InventoryCollector(probe, new FakeInstalledUpdates(), roles, new FakeEnvironment(), software: Source(runner)).CollectAsync(TestContext.Current.CancellationToken);
        var without = await new InventoryCollector(probe, new FakeInstalledUpdates(), roles, new FakeEnvironment()).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Single(with.InstalledSoftware!);
        Assert.Equal(SoftwareSupportState.Supported, with.Software!.State);
        Assert.Null(without.InstalledSoftware);
        Assert.Null(without.Software);
    }

    [Fact]
    public async Task SwitchedOffCentrally_WinGetIsNotRun_AndNoSoftwareIsReported()
    {
        var probe = new FakeSystemProbe();
        var roles = new RoleDetector(probe, Options.Create(new InventoryOptions()));
        var runner = new FakeRunner { Result = (0, Json(new SoftwareInventoryReport { Software = [Program()], Support = new SoftwareSupport { State = SoftwareSupportState.Supported } })) };
        var releases = new FakeReleaseSync { Current = new ReleaseList(1, [], SoftwareDisabled: true) };

        var inventory = await new InventoryCollector(probe, new FakeInstalledUpdates(), roles, new FakeEnvironment(), software: Source(runner), releases: releases).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Null(runner.Command);
        Assert.Null(inventory.InstalledSoftware);
        Assert.Null(inventory.Software);
    }

    [Fact]
    public async Task Runner_WithoutTheHelperNextToTheAgent_Throws()
    {
        // The test folder has no SUSyphos.Agent.WinGet.exe; nothing is started.
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, WinGetHelperRunner.FileName)));

        await Assert.ThrowsAsync<FileNotFoundException>(() => new WinGetHelperRunner().RunAsync(["inventory"], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    internal sealed class FakeRunner : IWinGetHelperRunner
    {
        public (int ExitCode, string Output)? Result { get; init; }

        public Exception? Throw { get; init; }

        public string? Command { get; private set; }

        public TimeSpan Timeout { get; private set; }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(int ExitCode, string Output)?> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            (Command, Timeout) = (string.Join(" ", arguments), timeout);
            Calls.Add(arguments);
            return Throw is not null ? Task.FromException<(int, string)?>(Throw) : Task.FromResult(Result);
        }
    }
}
