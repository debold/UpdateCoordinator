using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.SelfUpdate;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Tests;

internal sealed class FakePackageClient : IAgentPackageClient
{
    public byte[] Package { get; set; } = [0xD0, 0xCF, 0x11, 0xE0, 9, 8, 7];

    public bool Accepted { get; set; } = true;

    public int Downloads { get; private set; }

    public async Task<bool> DownloadAsync(AgentIdentity identity, string version, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        Downloads++;
        if (Accepted)
        {
            await target.WriteAsync(Package, cancellationToken);
        }

        return Accepted;
    }
}

internal sealed class FakePackageInstaller : IPackageInstaller
{
    public int? ExitCode { get; set; } = 0;

    public List<string> Installed { get; } = [];

    public Task<int?> InstallAsync(string packagePath, string logPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Installed.Add(packagePath);
        return Task.FromResult(ExitCode);
    }
}

internal sealed class InMemoryUpdateStateStore : IAgentUpdateStateStore
{
    public AgentUpdateState? State { get; set; }

    public AgentUpdateState? Load() => State;

    public void Save(AgentUpdateState state) => State = state;

    public void Delete() => State = null;
}

public sealed class AgentSelfUpdaterTests : IDisposable
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UpdateCoordinator.Tests", Guid.NewGuid().ToString("N"), "Update");
    private readonly FakePackageClient _packages = new();
    private readonly FakePackageInstaller _installer = new();
    private readonly InMemoryUpdateStateStore _state = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task AnnouncedUpdate_IsVerifiedAndInstalled()
    {
        // Anything placed in the folder beforehand is gone: the folder is recreated with a restricted ACL.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "planted.msi"), "x", TestContext.Current.CancellationToken);
        var updater = Create("0.3.0");

        Assert.True(await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0")), TestContext.Current.CancellationToken));

        Assert.Single(_installer.Installed);
        Assert.False(File.Exists(Path.Combine(_directory, "planted.msi")));
        Assert.True(new DirectoryInfo(_directory).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal(new AgentUpdateState("0.4.0", _time.GetUtcNow().UtcDateTime, Pending: true, Error: null), _state.State);
    }

    [Fact]
    public async Task PackageWithWrongHash_IsNeverInstalled()
    {
        var updater = Create("0.3.0");

        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0") with { Sha256 = new string('a', 64) }), TestContext.Current.CancellationToken);

        Assert.Empty(_installer.Installed);
        Assert.Contains("SHA-256", _state.State?.Error, StringComparison.Ordinal);
        Assert.Contains("SHA-256", updater.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageWithWrongSize_IsNeverInstalled()
    {
        var updater = Create("0.3.0");

        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0") with { Size = _packages.Package.Length + 1 }), TestContext.Current.CancellationToken);

        Assert.Empty(_installer.Installed);
        Assert.NotNull(_state.State?.Error);
    }

    [Theory]
    [InlineData("0.4.0", "abc", 7)]
    [InlineData("0.4.0", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", 7)]
    [InlineData("..\\..\\evil", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7)]
    [InlineData("0.4.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    [InlineData("0.4.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 600L * 1024 * 1024)]
    public async Task InvalidAnnouncements_AreIgnored(string version, string sha256, long size)
    {
        var updater = Create("0.3.0");

        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(new AgentUpdate(version, sha256, size)), TestContext.Current.CancellationToken);

        Assert.Equal(0, _packages.Downloads);
        Assert.Null(_state.State);
    }

    [Fact]
    public async Task FailedAttempt_IsRetriedOnlyAfterTheDelay()
    {
        _installer.ExitCode = 1603;
        var updater = Create("0.3.0");

        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0")), TestContext.Current.CancellationToken);
        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0")), TestContext.Current.CancellationToken);

        Assert.Single(_installer.Installed);
        Assert.Contains("1603", updater.LastError, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0")), TestContext.Current.CancellationToken);

        Assert.Equal(2, _installer.Installed.Count);
    }

    [Fact]
    public async Task NoAnnouncement_DisabledOrNotApproved_DoesNothing()
    {
        var updater = Create("0.3.0");
        await updater.UpdateIfAnnouncedAsync(Identity, Heartbeat(null), TestContext.Current.CancellationToken);
        await updater.UpdateIfAnnouncedAsync(Identity, new HeartbeatResponse(RegistrationState.Pending, AgentUpdate: Announce("0.4.0")), TestContext.Current.CancellationToken);
        await Create("0.3.0", enabled: false).UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0")), TestContext.Current.CancellationToken);

        Assert.Equal(0, _packages.Downloads);
    }

    [Fact]
    public async Task RejectedCredentials_AreReported()
    {
        _packages.Accepted = false;

        Assert.False(await Create("0.3.0").UpdateIfAnnouncedAsync(Identity, Heartbeat(Announce("0.4.0")), TestContext.Current.CancellationToken));
        Assert.Empty(_installer.Installed);
    }

    [Fact]
    public void AfterRestart_RunningTheNewVersion_ClearsTheState()
    {
        _state.State = new AgentUpdateState("0.4.0", _time.GetUtcNow().UtcDateTime, Pending: true, Error: null);

        Assert.Null(Create("0.4.0+3f2a").LastError);
        Assert.Null(_state.State);
    }

    [Fact]
    public void AfterRestart_RunningTheOldVersion_ReportsTheFailure()
    {
        _state.State = new AgentUpdateState("0.4.0", _time.GetUtcNow().UtcDateTime, Pending: true, Error: null);

        Assert.Contains("did not complete", Create("0.3.0").LastError, StringComparison.Ordinal);
        Assert.False(_state.State!.Pending);
    }

    [Fact]
    public void StateStore_IgnoresTamperedFiles()
    {
        var path = Path.Combine(Path.GetDirectoryName(_directory)!, "agent-update.json");
        var store = new JsonAgentUpdateStateStore(path);
        Assert.Null(store.Load());

        store.Save(new AgentUpdateState("0.4.0", DateTime.UtcNow, true, null));
        Assert.Equal("0.4.0", store.Load()?.Version);

        File.WriteAllText(path, "{ not json");
        Assert.Null(store.Load());
        File.WriteAllText(path, new string('x', 5000));
        Assert.Null(store.Load());
        store.Delete();
        Assert.Null(store.Load());
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_directory)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static AgentSelfUpdater CreateDisabled(string version) => new(
        new FakePackageClient(),
        new FakePackageInstaller(),
        new InMemoryUpdateStateStore(),
        new FakeEnvironment { AgentVersion = version },
        Options.Create(new AgentUpdateOptions { Enabled = false, Directory = Path.Combine(Path.GetTempPath(), "UpdateCoordinator.Tests", Guid.NewGuid().ToString("N")) }),
        TimeProvider.System,
        NullLogger<AgentSelfUpdater>.Instance);

    private AgentSelfUpdater Create(string runningVersion, bool enabled = true) => new(
        _packages,
        _installer,
        _state,
        new FakeEnvironment { AgentVersion = runningVersion },
        Options.Create(new AgentUpdateOptions { Enabled = enabled, Directory = _directory }),
        _time,
        NullLogger<AgentSelfUpdater>.Instance);

    private AgentUpdate Announce(string version) => new(version, Convert.ToHexStringLower(SHA256.HashData(_packages.Package)), _packages.Package.Length);

    private static HeartbeatResponse Heartbeat(AgentUpdate? update) => new(RegistrationState.Approved, AgentUpdate: update);
}
