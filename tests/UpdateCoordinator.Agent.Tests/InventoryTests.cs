using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Tests;

internal sealed class FakeInstalledUpdates : IInstalledUpdateSource
{
    public IReadOnlyList<RawInstalledUpdate>? Updates { get; set; } = [];

    public int Calls { get; private set; }

    public Task<IReadOnlyList<RawInstalledUpdate>?> GetInstalledUpdatesAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Updates);
    }
}

internal sealed class FakeSidReader : IComputerSidReader
{
    public string? Sid { get; set; } = "S-1-5-21-1004336348-1177238915-682003330-1104";

    public string? GetComputerSid(string? domainName) => domainName is null ? null : Sid;
}

public sealed class InventoryCollectorTests
{
    private readonly FakeSystemProbe _probe = new();
    private readonly FakeInstalledUpdates _updates = new();

    [Fact]
    public async Task Collect_ReadsOperatingSystemDomainAndRoles()
    {
        _probe.Values[(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")] = "Windows Server 2022 Standard";
        _probe.Values[(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID")] = "ServerStandard";
        _probe.Values[(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")] = "21H2";
        _probe.Services.Add("vmms");

        var inventory = await CollectAsync();

        Assert.Equal("SRV01", inventory.HostName);
        Assert.Equal("example.local", inventory.DomainName);
        Assert.StartsWith("S-1-5-21-", inventory.ComputerSid, StringComparison.Ordinal);
        Assert.Equal("Windows Server 2022 Standard", inventory.OperatingSystem.ProductName);
        Assert.Equal("ServerStandard", inventory.OperatingSystem.EditionId);
        Assert.Equal("21H2", inventory.OperatingSystem.DisplayVersion);
        Assert.Equal("10.0.20348.2700", inventory.OperatingSystem.Version);
        Assert.Equal(["HyperVHost"], inventory.Roles);
        Assert.False(inventory.UpdateSource.UsesWsus);
    }

    [Theory]
    [InlineData("Server 2022 Datacenter", "Server 2022 Datacenter")]
    [InlineData("Line\u0001Break\nName", "Line Break Name")]
    [InlineData("   ", "Windows")]
    public async Task Collect_SanitizesProductName(string raw, string expected)
    {
        _probe.Values[(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")] = raw;

        Assert.Equal(expected, (await CollectAsync()).OperatingSystem.ProductName);
    }

    [Theory]
    [InlineData("Server Standard")]
    [InlineData("<script>")]
    public async Task Collect_DropsInvalidEditionId(string raw)
    {
        _probe.Values[(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID")] = raw;

        Assert.Null((await CollectAsync()).OperatingSystem.EditionId);
    }

    [Fact]
    public async Task Collect_DetectsWsusFromPolicy()
    {
        _probe.Values[(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer")] = "1";
        _probe.Values[(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer")] = "https://wsus.example.local:8531";

        var source = (await CollectAsync()).UpdateSource;

        Assert.True(source.UsesWsus);
        Assert.Equal("https://wsus.example.local:8531", source.WsusServer);
    }

    [Fact]
    public async Task Collect_WsusServerWithoutUseFlag_IsNotWsus()
    {
        _probe.Values[(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer")] = "https://wsus.example.local:8531";

        Assert.False((await CollectAsync()).UpdateSource.UsesWsus);
    }

    [Fact]
    public async Task Collect_WhenUpdatesCannotBeRead_SendsNullInsteadOfEmpty()
    {
        _updates.Updates = null;

        Assert.Null((await CollectAsync()).InstalledUpdates);
    }

    [Fact]
    public void Normalize_ParsesKbAndClassification()
    {
        var id = Guid.NewGuid();

        var update = Assert.Single(InventoryCollector.Normalize([new RawInstalledUpdate(id.ToString(), 3, ["5034129"], "2024-01 Cumulative Update", "Security Updates")]));

        Assert.Equal(id, update.UpdateId);
        Assert.Equal(3, update.Revision);
        Assert.Equal("5034129", update.KbArticle);
        Assert.Equal("Security Updates", update.Classification);
    }

    [Theory]
    [InlineData("KB5034129", "5034129")]
    [InlineData("kb5034129", "5034129")]
    [InlineData("KBabc", null)]
    [InlineData("12345678901", null)]
    public void Normalize_AcceptsOnlyNumericKb(string raw, string? expected)
    {
        var update = Assert.Single(InventoryCollector.Normalize([new RawInstalledUpdate(Guid.NewGuid().ToString(), 1, [raw], "Title", null)]));

        Assert.Equal(expected, update.KbArticle);
    }

    [Fact]
    public void Normalize_DropsInvalidAndDuplicateEntries()
    {
        var id = Guid.NewGuid().ToString();
        var raw = new List<RawInstalledUpdate>
        {
            new("not-a-guid", 1, [], "Broken", null),
            new(Guid.NewGuid().ToString(), -1, [], "Negative revision", null),
            new(id, 1, [], "Original", null),
            new(id, 1, [], "Duplicate", null),
            new(id, 2, [], "Newer revision", null),
        };

        var normalized = InventoryCollector.Normalize(raw);

        Assert.Equal(2, normalized.Count);
        Assert.All(normalized, u => Assert.Equal(Guid.Parse(id), u.UpdateId));
    }

    [Fact]
    public void Normalize_SanitizesTitles()
    {
        var update = Assert.Single(InventoryCollector.Normalize([new RawInstalledUpdate(Guid.NewGuid().ToString(), 1, [], "Bad\u0000Title" + new string('x', 600), null)]));

        Assert.DoesNotContain('\u0000', update.Title);
        Assert.Equal(512, update.Title.Length);
    }

    [Fact]
    public void Normalize_CapsNumberOfUpdates()
    {
        var raw = Enumerable.Range(0, InventoryRequest.MaxInstalledUpdates + 10)
            .Select(_ => new RawInstalledUpdate(Guid.NewGuid().ToString(), 1, [], "Title", null))
            .ToList();

        Assert.Equal(InventoryRequest.MaxInstalledUpdates, InventoryCollector.Normalize(raw).Count);
    }

    private Task<InventoryRequest> CollectAsync()
    {
        var environment = new FakeEnvironment();
        var roles = new RoleDetector(_probe, Options.Create(new InventoryOptions()));
        return new InventoryCollector(_probe, new FakeSidReader(), _updates, roles, environment).CollectAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class InventorySchedulerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InventoryScheduler _scheduler;

    public InventorySchedulerTests()
    {
        _scheduler = new InventoryScheduler(
            Options.Create(new InventoryOptions { CollectInterval = TimeSpan.FromHours(1), MaxSendInterval = TimeSpan.FromDays(1) }),
            _time);
    }

    [Fact]
    public void FirstCollection_IsDueAndSent()
    {
        Assert.True(_scheduler.IsCollectionDue);
        Assert.True(_scheduler.ShouldSend(Inventory("SRV01")));
    }

    [Fact]
    public void UnchangedInventory_IsNotSentAgain()
    {
        SendOnce(Inventory("SRV01"));
        _time.Advance(TimeSpan.FromHours(1));

        Assert.True(_scheduler.IsCollectionDue);
        Assert.False(_scheduler.ShouldSend(Inventory("SRV01")));
    }

    [Fact]
    public void CollectionIsNotDueBeforeInterval()
    {
        SendOnce(Inventory("SRV01"));
        _time.Advance(TimeSpan.FromMinutes(59));

        Assert.False(_scheduler.IsCollectionDue);
    }

    [Fact]
    public void ChangedInventory_IsSent()
    {
        SendOnce(Inventory("SRV01"));
        _time.Advance(TimeSpan.FromHours(1));

        Assert.True(_scheduler.ShouldSend(Inventory("SRV01-RENAMED")));
    }

    [Fact]
    public void UnchangedInventory_IsSentAfterMaxInterval()
    {
        SendOnce(Inventory("SRV01"));
        _time.Advance(TimeSpan.FromDays(1));

        Assert.True(_scheduler.ShouldSend(Inventory("SRV01")));
    }

    [Fact]
    public void FailedSend_IsRetriedWithNextCycle()
    {
        Assert.True(_scheduler.ShouldSend(Inventory("SRV01")));
        _scheduler.MarkSendFailed();

        Assert.True(_scheduler.IsCollectionDue);
        Assert.True(_scheduler.ShouldSend(Inventory("SRV01")));
    }

    [Fact]
    public void Reset_ForcesNewSend()
    {
        SendOnce(Inventory("SRV01"));

        _scheduler.Reset();

        Assert.True(_scheduler.IsCollectionDue);
        Assert.True(_scheduler.ShouldSend(Inventory("SRV01")));
    }

    private void SendOnce(InventoryRequest inventory)
    {
        Assert.True(_scheduler.ShouldSend(inventory));
        _scheduler.MarkSent();
    }

    internal static InventoryRequest Inventory(string host) => new()
    {
        HostName = host,
        OperatingSystem = new OperatingSystemInfo { ProductName = "Windows Server", Version = "10.0.20348.1" },
        UpdateSource = new UpdateSourceInfo(),
        InstalledUpdates = [],
    };
}

public sealed class InventoryReporterTests
{
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    private readonly FakeCentralServerClient _client = new();
    private readonly FakeInstalledUpdates _updates = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Report_SendsFirstInventory()
    {
        var reporter = Create();

        Assert.True(await reporter.ReportIfDueAsync(Identity, TestContext.Current.CancellationToken));

        Assert.Single(_client.Inventories);
    }

    [Fact]
    public async Task Report_WhenNotDue_DoesNotCollect()
    {
        var reporter = Create();
        await reporter.ReportIfDueAsync(Identity, TestContext.Current.CancellationToken);

        await reporter.ReportIfDueAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal(1, _updates.Calls);
        Assert.Single(_client.Inventories);
    }

    [Fact]
    public async Task Report_WhenCredentialsRejected_ReturnsFalseAndRetries()
    {
        _client.AcceptInventory = false;
        var reporter = Create();

        Assert.False(await reporter.ReportIfDueAsync(Identity, TestContext.Current.CancellationToken));

        _client.AcceptInventory = true;
        Assert.True(await reporter.ReportIfDueAsync(Identity, TestContext.Current.CancellationToken));
        Assert.Equal(2, _client.Inventories.Count);
    }

    private InventoryReporter Create()
    {
        var probe = new FakeSystemProbe();
        var options = Options.Create(new InventoryOptions());
        var collector = new InventoryCollector(probe, new FakeSidReader(), _updates, new RoleDetector(probe, options), new FakeEnvironment());
        return new InventoryReporter(collector, new InventoryScheduler(options, _time), _client, NullLogger<InventoryReporter>.Instance);
    }
}
