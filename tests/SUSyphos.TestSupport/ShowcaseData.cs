using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;
using SUSyphos.Server.Rollouts;

namespace SUSyphos.TestSupport;

/// <summary>
/// A clean, realistic lab for the screenshots of the help and the README (Marc, 2026-11-08): servers with current builds
/// and agents, and rollouts at every stage (one done in every ring, one rolling out in Test, one waiting at the manual gate
/// before Production, one just started, one with a failure in Pilot). Edge cases such as very long names stay in
/// <see cref="DemoData"/>, which the layout tests use.
/// </summary>
public static class ShowcaseData
{
    /// <summary>Host name of the server the server pages show.</summary>
    public const string MainServer = "SQL01";

    private const string Domain = "corp.example";
    private const int SqlCluster = 10, WebFarm = 11, FileServers = 12;

    private static readonly (string Host, string Os, string Build, int Ring, int? Group, string Roles)[] Servers =
    [
        ("DC01", "Windows Server 2022 Datacenter", "10.0.20348.4405", RingEntity.PilotId, null, "DomainController"),
        ("APP01", "Windows Server 2025 Standard", "10.0.26100.6899", RingEntity.PilotId, null, ""),
        ("DC02", "Windows Server 2022 Datacenter", "10.0.20348.4405", RingEntity.TestId, null, "DomainController"),
        ("SQL02", "Windows Server 2022 Datacenter", "10.0.20348.4405", RingEntity.TestId, SqlCluster, "SqlServer"),
        ("APP02", "Windows Server 2019 Standard", "10.0.17763.7922", RingEntity.TestId, null, ""),
        (MainServer, "Windows Server 2022 Datacenter", "10.0.20348.4405", RingEntity.ProductionId, SqlCluster, "SqlServer"),
        ("FS01", "Windows Server 2019 Standard", "10.0.17763.7922", RingEntity.ProductionId, FileServers, ""),
        ("FS02", "Windows Server 2016 Standard", "10.0.14393.8422", RingEntity.ProductionId, FileServers, ""),
        ("WEB01", "Windows Server 2022 Standard", "10.0.20348.4405", RingEntity.ProductionId, WebFarm, ""),
        ("WEB02", "Windows Server 2022 Standard", "10.0.20348.4405", RingEntity.ProductionId, WebFarm, ""),
        ("RDS01", "Windows Server 2025 Datacenter", "10.0.26100.6899", RingEntity.ProductionId, null, ""),
        ("BACKUP01", "Windows Server 2019 Standard", "10.0.17763.7922", RingEntity.ProductionId, null, ""),
    ];

    /// <summary>The updates, the builds that need them, and the stage of their rollout per ring (Pilot, Test, Production).</summary>
    private static readonly Update[] Updates =
    [
        new("5068787", "2026-11 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5068787)", "Security Updates", "Critical", "20348", Stages.Released, Stages.Released, Stages.Waiting, 4),
        new("5068791", "2026-11 Cumulative Update for Windows Server 2019 for x64-based Systems (KB5068791)", "Security Updates", "Important", "17763", Stages.Released, Stages.Released, Stages.Gate, 4),
        new("5068779", "2026-11 Servicing Stack Update for Windows Server 2025 for x64-based Systems (KB5068779)", "Security Updates", "Important", "26100", Stages.Released, Stages.Waiting, Stages.Waiting, 1),
        new("5068870", "2026-11 Cumulative Update for .NET Framework 3.5, 4.8 and 4.8.1 for Microsoft server operating system version 21H2 for x64 (KB5068870)", "Updates", null, "20348", Stages.Released, Stages.Alarm, Stages.Waiting, 3),
        new("890830", "Windows Malicious Software Removal Tool x64 - v5.147 (KB890830)", "Update Rollups", null, "", Stages.Released, Stages.Released, Stages.Released, 8),
        new("2267602", "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.441.98.0)", "Definition Updates", null, "", Stages.Released, Stages.Released, Stages.Released, 1),
    ];

    private enum Stages
    {
        Waiting,
        Released,
        Gate,
        Alarm,
    }

    public static async Task<Guid> SeedAsync(ServerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.StartServer();
        var now = DateTime.UtcNow;
        var ids = new Dictionary<string, Guid>();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            db.Groups.Add(new GroupEntity { Id = SqlCluster, Name = "SQL cluster", Coordinated = true, LeasePercent = 50, LeaseMaximum = 1 });
            db.Groups.Add(new GroupEntity { Id = WebFarm, Name = "Web farm", Coordinated = true, LeasePercent = 50, LeaseMaximum = 1 });
            db.Groups.Add(new GroupEntity { Id = FileServers, Name = "File servers" });
            var minutes = 1;
            foreach (var s in Servers)
            {
                var id = Guid.NewGuid();
                ids[s.Host] = id;
                db.Clients.Add(new ClientEntity
                {
                    Id = id,
                    SecretHash = [1],
                    HostName = s.Host,
                    DomainName = Domain,
                    OsVersion = s.Build,
                    OsProductName = s.Os,
                    AgentVersion = s.Host == "FS02" ? "0.8.1" : "0.8.2",
                    State = RegistrationState.Approved,
                    ManualRingId = s.Ring,
                    LastHeartbeatAtUtc = now.AddMinutes(-(minutes++ % 4) - 1),
                    LastBootAtUtc = now.AddDays(-6).AddHours(-(minutes % 5)),
                    RegisteredAtUtc = now.AddDays(-40),
                    TimeZoneId = "W. Europe Standard Time",
                    Roles = s.Roles.Length == 0 ? [] : [s.Roles],
                    NextSearchAtUtc = now.AddHours(3),
                    NextInventoryAtUtc = now.AddHours(5),
                });
                if (s.Group is { } group)
                {
                    db.ClientGroups.Add(new ClientGroupEntity { ClientId = id, GroupId = group, Manual = true });
                }
            }

            db.Clients.Add(new ClientEntity
            {
                Id = Guid.NewGuid(),
                SecretHash = [1],
                HostName = "APP03",
                DomainName = Domain,
                OsVersion = "10.0.26100.6899",
                OsProductName = "Windows Server 2025 Standard",
                AgentVersion = "0.8.2",
                State = RegistrationState.Pending,
                LastHeartbeatAtUtc = now.AddMinutes(-3),
                RegisteredAtUtc = now.AddHours(-1),
                TimeZoneId = "W. Europe Standard Time",
            });

            db.AgentPackages.AddRange(
                new SUSyphos.Server.AgentPackages.AgentPackageEntity { Version = "0.8.2", Source = SUSyphos.Server.AgentPackages.AgentPackageSource.GitHub, FileName = "SUSyphos.Agent-0.8.2.msi", Sha256 = new string('a', 64), Size = 70_000_000, AddedAtUtc = now.AddDays(-2), ReleasedThroughRingOrder = SUSyphos.Server.AgentPackages.AgentPackageEntity.AllRings, ReleasedAtUtc = now.AddDays(-2), ReleasedBy = "marc" },
                new SUSyphos.Server.AgentPackages.AgentPackageEntity { Version = "0.8.1", Source = SUSyphos.Server.AgentPackages.AgentPackageSource.GitHub, FileName = "SUSyphos.Agent-0.8.1.msi", Sha256 = new string('b', 64), Size = 70_000_000, AddedAtUtc = now.AddDays(-9) });

            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<AssignmentService>().RecalculateAsync(null, CancellationToken.None);
        }

        var available = Updates.Select(u => (Update: u, Available: new AvailableUpdate
        {
            UpdateId = Guid.NewGuid(),
            Revision = 1,
            KbArticle = u.Kb,
            Title = u.Title,
            Classification = u.Classification,
            MsrcSeverity = u.Severity,
            MaxDownloadSizeBytes = 400_000_000,
            RebootBehavior = u.Classification == "Definition Updates" ? RebootBehavior.NeverReboots : RebootBehavior.CanRequestReboot,
        })).ToList();

        // 1. Every server reports what its build needs; the default start rule starts each rollout in Pilot.
        foreach (var s in Servers)
        {
            await ReportAsync(factory, ids[s.Host], available.Where(a => Needs(s.Build, a.Update)).Select(a => a.Available).ToList());
        }

        // 2. The stage of each rollout, as if it had run for some days.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            foreach (var (update, item) in available)
            {
                var started = now.AddDays(-update.DaysAgo);
                var rollout = await db.Rollouts.SingleAsync(r => r.UpdateId == item.UpdateId);
                rollout.StartedAtUtc = started;
                rollout.Override = update.Classification == "Definition Updates";
                var stages = new[] { (RingEntity.PilotId, update.Pilot, 0.0), (RingEntity.TestId, update.Test, 0.4), (RingEntity.ProductionId, update.Production, 0.7) };
                foreach (var (ring, stage, share) in stages)
                {
                    var row = await db.RolloutRings.SingleOrDefaultAsync(r => r.UpdateId == item.UpdateId && r.RingId == ring);
                    if (row is null)
                    {
                        row = new RolloutRingEntity { UpdateId = item.UpdateId, Revision = 1, RingId = ring };
                        db.RolloutRings.Add(row);
                    }

                    var at = started.AddDays(update.DaysAgo * share);
                    row.State = stage switch
                    {
                        Stages.Released => RingRolloutState.Released,
                        Stages.Gate => RingRolloutState.AwaitingApproval,
                        Stages.Alarm => RingRolloutState.Alarm,
                        _ => RingRolloutState.Waiting,
                    };
                    row.ReleasedAtUtc = stage == Stages.Released ? at : null;
                    row.StateChangedAtUtc = at;
                    row.Reason = stage == Stages.Alarm ? "success rate 0 % in Pilot, at least 90 % required" : null;
                }
            }

            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ReleasePlanner>().SyncAsync(null, CancellationToken.None);
        }

        // 3. Installation results where the rollout has reached the ring and some days have passed.
        foreach (var s in Servers)
        {
            var results = new List<InstallResult>();
            foreach (var (update, item) in available.Where(a => Needs(s.Build, a.Update)))
            {
                var stage = s.Ring == RingEntity.PilotId ? update.Pilot : s.Ring == RingEntity.TestId ? update.Test : update.Production;
                if (stage != Stages.Released || (update.Kb == "5068787" && s.Host == "SQL02") || update.Kb == "5068779")
                {
                    continue;
                }

                var failed = update.Kb == "5068870";
                results.Add(new InstallResult { UpdateId = item.UpdateId, Revision = 1, ResultCode = failed ? InstallResultCode.Failed : InstallResultCode.Succeeded, HResult = failed ? unchecked((int)0x80070643) : 0, RebootRequired = !failed && update.Classification != "Definition Updates" });
            }

            if (results.Count > 0)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<InstallationService>().StoreAsync(ids[s.Host], new InstallReport
                {
                    RunId = Guid.NewGuid(),
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-25),
                    FinishedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    Results = results,
                    RebootRequired = results.Any(r => r.RebootRequired),
                }, CancellationToken.None);

                // The next search no longer lists what was installed.
                var installed = results.Where(r => r.ResultCode == InstallResultCode.Succeeded).Select(r => r.UpdateId).ToHashSet();
                await ReportAsync(factory, ids[s.Host], available.Where(a => Needs(s.Build, a.Update) && !installed.Contains(a.Available.UpdateId)).Select(a => a.Available).ToList());
            }
        }

        // 4. The main server: Windows Update history, a few weeks of searches, current security data.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<InventoryService>().StoreAsync(ids[MainServer], new InventoryRequest
            {
                HostName = MainServer,
                DomainName = Domain,
                TimeZoneId = "W. Europe Standard Time",
                OperatingSystem = new OperatingSystemInfo { ProductName = "Windows Server 2022 Datacenter", EditionId = "ServerDatacenter", DisplayVersion = "21H2", Version = "10.0.20348.4405" },
                Roles = ["SqlServer"],
                UpdateSource = new UpdateSourceInfo { AutomaticUpdates = new AutomaticUpdatesInfo { Mode = AutomaticUpdateMode.Disabled, ManagedByAgent = true } },
                UpdateHistory =
                [
                    new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = "2026-10 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5066835)", Date = DateTimeOffset.UtcNow.AddDays(-24), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded, ClientApplicationId = UpdateHistoryEntry.AgentClientApplicationId },
                    new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = "2026-10 Cumulative Update for .NET Framework 3.5, 4.8 and 4.8.1 for Microsoft server operating system version 21H2 for x64 (KB5066131)", Date = DateTimeOffset.UtcNow.AddDays(-24).AddMinutes(-12), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded, ClientApplicationId = UpdateHistoryEntry.AgentClientApplicationId },
                    new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = "Windows Malicious Software Removal Tool x64 - v5.146 (KB890830)", Date = DateTimeOffset.UtcNow.AddDays(-25), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded, ClientApplicationId = UpdateHistoryEntry.AgentClientApplicationId },
                    new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = "2026-09 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5065432)", Date = DateTimeOffset.UtcNow.AddDays(-54), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded, ClientApplicationId = UpdateHistoryEntry.AgentClientApplicationId },
                ],
            }, CancellationToken.None);

            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            for (var day = 14; day >= 1; day--)
            {
                db.ClientActivities.Add(new ClientActivityEntity { ClientId = ids[MainServer], AtUtc = now.AddDays(-day), Kind = ClientActivityKind.Search, Detail = "Update search succeeded." });
                db.ClientActivities.Add(new ClientActivityEntity { ClientId = ids[MainServer], AtUtc = now.AddDays(-day).AddMinutes(4), Kind = ClientActivityKind.Inventory, Detail = "Inventory received." });
            }

            // MSRC data of the last two months: the servers run the October build, only FS02 is still on September.
            foreach (var (build, october, september) in new[] { (14393, 8519, 8422), (17763, 7922, 7840), (20348, 4405, 4294), (26100, 6899, 6584) })
            {
                db.SecurityBaselines.Add(new SecurityBaselineEntity { Build = build, Month = "2026-Oct", ReleasedAtUtc = now.AddDays(-26), Revision = october, Kb = "5066835" });
                db.SecurityBaselines.Add(new SecurityBaselineEntity { Build = build, Month = "2026-Sep", ReleasedAtUtc = now.AddDays(-54), Revision = september, Kb = "5065432" });
                db.SecurityFixes.Add(new SecurityFixEntity { Cve = "CVE-2026-69354", Month = "2026-Oct", Build = build, Revision = october, Kb = "5066835" });
                db.SecurityFixes.Add(new SecurityFixEntity { Cve = "CVE-2026-61117", Month = "2026-Sep", Build = build, Revision = september, Kb = "5065432" });
            }

            db.SecurityVulnerabilities.Add(new SecurityVulnerabilityEntity { Cve = "CVE-2026-69354", Month = "2026-Oct", Title = "Windows TCP/IP Remote Code Execution Vulnerability", Severity = "Critical", Impact = "Remote Code Execution", CvssScore = 9.8 });
            db.SecurityVulnerabilities.Add(new SecurityVulnerabilityEntity { Cve = "CVE-2026-61117", Month = "2026-Sep", Title = "Windows Kernel Elevation of Privilege Vulnerability", Severity = "Important", Impact = "Elevation of Privilege", CvssScore = 7.8 });

            // The servers restarted after their installations; one waits for its window.
            foreach (var client in await db.Clients.ToListAsync())
            {
                client.RebootPending = client.HostName == "DC02";
            }

            await db.SaveChangesAsync();
        }

        return ids[MainServer];
    }

    private static bool Needs(string osVersion, Update update) => update.Build.Length == 0 || osVersion.Contains($".{update.Build}.", StringComparison.Ordinal);

    private static async Task ReportAsync(ServerFactory factory, Guid id, List<AvailableUpdate> missing)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UpdateReportService>().StoreAsync(id, new UpdateReport
        {
            StartedAt = DateTimeOffset.UtcNow,
            SearchSucceeded = true,
            HealthChecks =
            [
                new HealthCheckResult { Name = "DiskSpace", Status = HealthStatus.Ok, Detail = "Free: 48200 MB" },
                new HealthCheckResult { Name = "AutomaticUpdates", Status = HealthStatus.Ok, Detail = "Windows does not install on its own" },
            ],
            AvailableUpdates = missing,
        }, CancellationToken.None);
    }

    private sealed record Update(string Kb, string Title, string Classification, string? Severity, string Build, Stages Pilot, Stages Test, Stages Production, int DaysAgo);
}
