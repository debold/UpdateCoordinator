using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Updates;
using UpdateCoordinator.Agent.Wua;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Tests;

internal sealed class FakeDiskSpace : IDiskSpace
{
    public long? SystemDriveFreeBytes { get; set; } = 50L * 1024 * 1024 * 1024;
}

internal sealed class FakeAvailableUpdateSearch : IAvailableUpdateSearch
{
    public Func<IReadOnlyList<WuaUpdate>> OnSearch { get; set; } = () => [];

    public int Calls { get; private set; }

    public Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(OnSearch());
    }
}

public sealed class PreFlightCheckTests
{
    private const string WuauservPath = @"SYSTEM\CurrentControlSet\Services\wuauserv";

    private readonly FakeSystemProbe _probe = new();
    private readonly FakeDiskSpace _disk = new();

    public PreFlightCheckTests()
    {
        _probe.Values[(WuauservPath, "Start")] = "3";
    }

    [Fact]
    public void HealthySystem_PassesAllChecks()
    {
        Assert.All(Run(), check => Assert.Equal(HealthStatus.Ok, check.Status));
    }

    [Fact]
    public void LowDiskSpace_Fails()
    {
        _disk.SystemDriveFreeBytes = 100L * 1024 * 1024;

        Assert.Equal(HealthStatus.Failed, Check(PreFlightCheck.DiskSpaceCheck).Status);
    }

    [Fact]
    public void UnknownDiskSpace_IsWarning()
    {
        _disk.SystemDriveFreeBytes = null;

        Assert.Equal(HealthStatus.Warning, Check(PreFlightCheck.DiskSpaceCheck).Status);
    }

    [Theory]
    [InlineData("4")]
    [InlineData(null)]
    public void DisabledOrMissingUpdateService_Fails(string? start)
    {
        if (start is null)
        {
            _probe.Values.Remove((WuauservPath, "Start"));
        }
        else
        {
            _probe.Values[(WuauservPath, "Start")] = start;
        }

        Assert.Equal(HealthStatus.Failed, Check(PreFlightCheck.UpdateServiceCheck).Status);
    }

    [Theory]
    [InlineData((int)UpdateSourceSelection.System, "searched there")]
    [InlineData((int)UpdateSourceSelection.WindowsUpdate, "Windows Update directly")]
    public void Wsus_IsWarningNotFailure(int selection, string expectedDetail)
    {
        _probe.Values[(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer")] = "1";
        _probe.Values[(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer")] = "https://wsus.example.local:8531";

        var check = Check(PreFlightCheck.UpdateSourceCheck, (UpdateSourceSelection)selection);

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains(expectedDetail, check.Detail, StringComparison.Ordinal);
    }

    private HealthCheckResult Check(string name, UpdateSourceSelection selection = UpdateSourceSelection.System) =>
        Run(selection).Single(check => check.Name == name);

    private IReadOnlyList<HealthCheckResult> Run(UpdateSourceSelection selection = UpdateSourceSelection.System) =>
        new PreFlightCheck(_probe, _disk, Options.Create(new UpdateOptions { Source = selection })).Run();
}

public sealed class UpdateReporterTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    private readonly FakeSystemProbe _probe = new();
    private readonly FakeDiskSpace _disk = new();
    private readonly FakeAvailableUpdateSearch _search = new();
    private readonly FakeCentralServerClient _client = new();
    private readonly FakeTimeProvider _time = new();

    public UpdateReporterTests()
    {
        _probe.Values[(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start")] = "3";
    }

    [Fact]
    public async Task SuccessfulSearch_IsReportedWithUpdates()
    {
        var id = Guid.NewGuid();
        _search.OnSearch = () => [new WuaUpdate(id.ToString(), 2, ["KB5034129"], "Cumulative Update", "Security Updates", 123_456m, 1, "Critical")];

        Assert.True(await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        var report = Assert.Single(_client.Reports);
        Assert.True(report.SearchSucceeded);
        var update = Assert.Single(report.AvailableUpdates!);
        Assert.Equal(id, update.UpdateId);
        Assert.Equal(123_456, update.MaxDownloadSizeBytes);
        Assert.Equal(RebootBehavior.AlwaysRequiresReboot, update.RebootBehavior);
        Assert.Equal("Critical", update.MsrcSeverity);
    }

    [Fact]
    public async Task FailedPreFlightCheck_SkipsSearch()
    {
        _disk.SystemDriveFreeBytes = 1;

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        var report = Assert.Single(_client.Reports);
        Assert.False(report.SearchSucceeded);
        Assert.Null(report.AvailableUpdates);
        Assert.Equal(0, _search.Calls);
        Assert.Contains(report.HealthChecks, check => check.Status == HealthStatus.Failed);
    }

    [Fact]
    public async Task FailedSearch_IsReportedWithErrorCode()
    {
        _search.OnSearch = () => throw new WuaException("offline", unchecked((int)0x8024402C));

        await Create().RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        var report = Assert.Single(_client.Reports);
        Assert.False(report.SearchSucceeded);
        Assert.Equal(unchecked((int)0x8024402C), report.SearchHResult);
        Assert.Null(report.AvailableUpdates);
    }

    [Fact]
    public async Task Run_IsNotRepeatedWithinInterval()
    {
        var reporter = Create();
        await reporter.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromHours(3));
        await reporter.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Single(_client.Reports);

        _time.Advance(TimeSpan.FromHours(1));
        await reporter.RunIfDueAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Equal(2, _client.Reports.Count);
    }

    [Fact]
    public async Task RejectedCredentials_ReturnFalseAndRunAgainNextCycle()
    {
        _client.AcceptReports = false;
        var reporter = Create();

        Assert.False(await reporter.RunIfDueAsync(Identity, TestContext.Current.CancellationToken));

        _client.AcceptReports = true;
        Assert.True(await reporter.RunIfDueAsync(Identity, TestContext.Current.CancellationToken));
        Assert.Equal(2, _search.Calls);
    }

    private UpdateReporter Create()
    {
        var options = Options.Create(new UpdateOptions());
        return new UpdateReporter(new PreFlightCheck(_probe, _disk, options), _search, _client, options, _time, NullLogger<UpdateReporter>.Instance);
    }
}

public sealed class AvailableUpdateFormatTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2_000_000_000_000)]
    public void ImplausibleSize_IsDropped(double size)
    {
        var update = Assert.Single(WireFormat.ToAvailableUpdates([Raw(size: (decimal)size)]));

        Assert.Null(update.MaxDownloadSizeBytes);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void UnknownRebootBehavior_IsDropped(int behavior)
    {
        Assert.Null(Assert.Single(WireFormat.ToAvailableUpdates([Raw(reboot: behavior)])).RebootBehavior);
    }

    [Theory]
    [InlineData("Critical", "Critical")]
    [InlineData("", null)]
    [InlineData("Very Important", null)]
    [InlineData("<b>", null)]
    public void Severity_IsOnlyKeptIfPlausible(string raw, string? expected)
    {
        Assert.Equal(expected, Assert.Single(WireFormat.ToAvailableUpdates([Raw(severity: raw)])).MsrcSeverity);
    }

    [Fact]
    public void NumberOfUpdates_IsCapped()
    {
        var raw = Enumerable.Range(0, UpdateReport.MaxAvailableUpdates + 5).Select(_ => Raw()).ToList();

        Assert.Equal(UpdateReport.MaxAvailableUpdates, WireFormat.ToAvailableUpdates(raw).Count);
    }

    private static WuaUpdate Raw(decimal? size = 1000, int? reboot = 0, string? severity = null) =>
        new(Guid.NewGuid().ToString(), 1, [], "Title", null, size, reboot, severity);
}

public sealed class ReleaseSyncTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    private readonly FakeCentralServerClient _client = new();

    [Fact]
    public async Task NewVersion_FetchesReleases()
    {
        var release = new Release(Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        _client.ReleaseListResult = new ReleaseList(3, [release]);
        var sync = Create();

        Assert.True(await sync.SyncAsync(Identity, new HeartbeatResponse(RegistrationState.Approved, 3), TestContext.Current.CancellationToken));

        Assert.Equal(3, sync.Current.Version);
        Assert.Equal([release], sync.Current.Releases);
    }

    [Fact]
    public async Task SameVersion_DoesNotFetchAgain()
    {
        _client.ReleaseListResult = new ReleaseList(3, [new Release(Guid.NewGuid(), 1, DateTimeOffset.UtcNow)]);
        var sync = Create();
        await sync.SyncAsync(Identity, new HeartbeatResponse(RegistrationState.Approved, 3), TestContext.Current.CancellationToken);

        await sync.SyncAsync(Identity, new HeartbeatResponse(RegistrationState.Approved, 3), TestContext.Current.CancellationToken);

        Assert.Equal(1, _client.ReleaseRequests);
    }

    [Fact]
    public async Task NoAnnouncedVersion_ClearsReleasesWithoutFetching()
    {
        _client.ReleaseListResult = new ReleaseList(3, [new Release(Guid.NewGuid(), 1, DateTimeOffset.UtcNow)]);
        var sync = Create();
        await sync.SyncAsync(Identity, new HeartbeatResponse(RegistrationState.Approved, 3), TestContext.Current.CancellationToken);

        await sync.SyncAsync(Identity, new HeartbeatResponse(RegistrationState.Pending), TestContext.Current.CancellationToken);

        Assert.Empty(sync.Current.Releases);
        Assert.Equal(1, _client.ReleaseRequests);
    }

    [Fact]
    public async Task RejectedCredentials_ReturnFalse()
    {
        _client.ReleaseListResult = null;

        Assert.False(await Create().SyncAsync(Identity, new HeartbeatResponse(RegistrationState.Approved, 1), TestContext.Current.CancellationToken));
    }

    private ReleaseSync Create() => new(_client, NullLogger<ReleaseSync>.Instance);
}
