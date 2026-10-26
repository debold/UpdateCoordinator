using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;

namespace SUSyphos.TestSupport;

/// <summary>
/// A lab with realistic content for dashboard snapshots and UI tests: servers in every ring and state, groups, updates in
/// rollout with installation results, Windows Update history, remote actions and live status. Data goes in through the
/// same services the agents use, so the pages show what a real installation would show.
/// </summary>
public static class DemoData
{
    /// <summary>Host name of the server most pages are tested with.</summary>
    public const string MainServer = "SQL01";

    private static readonly (string Kb, string Title, string Classification, string? Severity)[] Updates =
    [
        ("5066835", "2026-10 Cumulative Update for Microsoft server operating system version 21H2 for x64-based Systems (KB5066835)", "Security Updates", "Critical"),
        ("5066780", "2026-10 Servicing Stack Update for Windows Server 2022 for x64-based Systems (KB5066780)", "Security Updates", "Important"),
        ("890830", "Windows Malicious Software Removal Tool x64 - v5.146 (KB890830)", "Update Rollups", null),
        ("2267602", "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.439.123.0) - Current Channel (Broad)", "Definition Updates", null),
        ("5066131", "2026-10 Cumulative Update for .NET Framework 3.5, 4.8 and 4.8.1 for Microsoft server operating system version 21H2 for x64 (KB5066131)", "Updates", null),
        ("", "Broadcom Inc. - Net - 226.0.145.0 (very long driver title to see how long titles wrap in tables and cards)", "Drivers", null),
    ];

    public static async Task<Guid> SeedAsync(ServerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.StartServer();
        var now = DateTime.UtcNow;
        var servers = new (string Host, string Domain, string Version, int Ring, RegistrationState State, int MinutesAgo, int? Group, bool Reboot)[]
        {
            ("LABVDC01", "lab.local", "10.0.20348.2762", RingEntity.PilotId, RegistrationState.Approved, 2, null, false),
            ("LABVDC02", "lab.local", "10.0.20348.2762", RingEntity.TestId, RegistrationState.Approved, 1, null, false),
            (MainServer, "lab.local", "10.0.20348.2762", RingEntity.PilotId, RegistrationState.Approved, 1, 10, true),
            ("SQL02", "lab.local", "10.0.17763.6414", RingEntity.ProductionId, RegistrationState.Approved, 4, 10, true),
            ("FS01", "lab.local", "10.0.14393.7428", RingEntity.ProductionId, RegistrationState.Approved, 3, null, false),
            ("APP01-WITH-A-LONG-NAME", "subsidiary.example.corporation.local", "10.0.26100.2033", RingEntity.TestId, RegistrationState.Approved, 1, 11, false),
            ("WEB01", "dmz.local", "10.0.20348.2700", RingEntity.ProductionId, RegistrationState.Approved, 2900, 11, false),
            ("WEB02", "dmz.local", "10.0.20348.2762", RingEntity.ProductionId, RegistrationState.Approved, 2, 11, false),
            ("NEWSRV01", "lab.local", "10.0.20348.2762", RingEntity.ProductionId, RegistrationState.Pending, 5, null, false),
        };

        var ids = new Dictionary<string, Guid>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            db.Groups.Add(new GroupEntity { Id = 10, Name = "SQL cluster", Coordinated = true, LeasePercent = 50, LeaseMaximum = 1 });
            db.Groups.Add(new GroupEntity { Id = 11, Name = "Web farm" });
            foreach (var s in servers)
            {
                var id = Guid.NewGuid();
                ids[s.Host] = id;
                db.Clients.Add(new ClientEntity
                {
                    Id = id,
                    SecretHash = [1],
                    HostName = s.Host,
                    DomainName = s.Domain,
                    OsVersion = s.Version,
                    AgentVersion = s.Host == "FS01" ? "0.6.0" : "0.7.1-alpha.0.12+3f2a9c1",
                    State = s.State,
                    ManualRingId = s.Ring,
                    LastHeartbeatAtUtc = now.AddMinutes(-s.MinutesAgo),
                    LastBootAtUtc = now.AddDays(-3).AddHours(-4),
                    RegisteredAtUtc = now.AddDays(-12),
                    OsProductName = "Windows Server 2022 Datacenter",
                    RebootPending = s.Reboot,
                    TimeZoneId = "W. Europe Standard Time",
                    Roles = s.Host.StartsWith("LABVDC", StringComparison.Ordinal) ? ["DomainController", "DnsServer"] : s.Host.StartsWith("SQL", StringComparison.Ordinal) ? ["SqlServer"] : [],
                    NextSearchAtUtc = now.AddHours(2),
                    NextInventoryAtUtc = now.AddHours(5),
                });
                if (s.Group is { } group)
                {
                    db.ClientGroups.Add(new ClientGroupEntity { ClientId = id, GroupId = group, Manual = true });
                }
            }

            // Agent packages: a newer one waiting for release (all buttons), the released one, and superseded ones (hidden).
            db.AgentPackages.AddRange(
                new SUSyphos.Server.AgentPackages.AgentPackageEntity { Version = "0.8.0", Source = SUSyphos.Server.AgentPackages.AgentPackageSource.Bundled, FileName = "SUSyphos.Agent-0.8.0.msi", Sha256 = new string('a', 64), Size = 70_000_000, AddedAtUtc = now.AddHours(-2) },
                new SUSyphos.Server.AgentPackages.AgentPackageEntity { Version = "0.7.1", Source = SUSyphos.Server.AgentPackages.AgentPackageSource.GitHub, FileName = "SUSyphos.Agent-0.7.1.msi", Sha256 = new string('b', 64), Size = 70_000_000, AddedAtUtc = now.AddDays(-1), ReleasedThroughRingOrder = SUSyphos.Server.AgentPackages.AgentPackageEntity.AllRings, ReleasedAtUtc = now.AddDays(-1), ReleasedBy = "marc" },
                new SUSyphos.Server.AgentPackages.AgentPackageEntity { Version = "0.7.0", Source = SUSyphos.Server.AgentPackages.AgentPackageSource.GitHub, FileName = "SUSyphos.Agent-0.7.0.msi", Sha256 = new string('c', 64), Size = 70_000_000, AddedAtUtc = now.AddDays(-2) },
                new SUSyphos.Server.AgentPackages.AgentPackageEntity { Version = "0.5.0", Source = SUSyphos.Server.AgentPackages.AgentPackageSource.Bundled, FileName = "SUSyphos.Agent-0.5.0.msi", Sha256 = new string('d', 64), Size = 70_000_000, AddedAtUtc = now.AddDays(-5) });

            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<AssignmentService>().RecalculateAsync(null, CancellationToken.None);
        }

        var available = Updates.Select(u => new AvailableUpdate
        {
            UpdateId = Guid.NewGuid(),
            Revision = 1,
            KbArticle = u.Kb.Length == 0 ? null : u.Kb,
            Title = u.Title,
            Classification = u.Classification,
            MsrcSeverity = u.Severity,
            MaxDownloadSizeBytes = 512_000_000,
            RebootBehavior = RebootBehavior.CanRequestReboot,
        }).ToList();

        foreach (var (host, id) in ids.Where(i => i.Key != "NEWSRV01"))
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var reports = scope.ServiceProvider.GetRequiredService<UpdateReportService>();
            await reports.StoreAsync(id, new UpdateReport
            {
                StartedAt = DateTimeOffset.UtcNow,
                SearchSucceeded = host != "WEB01",
                SearchHResult = host == "WEB01" ? unchecked((int)0x8024402C) : null,
                HealthChecks =
                [
                    new HealthCheckResult { Name = "DiskSpace", Status = host == "FS01" ? HealthStatus.Warning : HealthStatus.Ok, Detail = host == "FS01" ? "Free: 3100 MB, below 4096 MB" : "Free: 51200 MB" },
                    new HealthCheckResult { Name = "AutomaticUpdates", Status = HealthStatus.Ok, Detail = "Windows does not install on its own" },
                ],
                AvailableUpdates = host == "WEB01" ? null : available,
            }, CancellationToken.None);
        }

        // Pilot results: one success, one failure, so rings and update pages show both.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installations = scope.ServiceProvider.GetRequiredService<InstallationService>();
            await installations.StoreAsync(ids["LABVDC01"], new InstallReport
            {
                RunId = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-40),
                FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                Results =
                [
                    new InstallResult { UpdateId = available[0].UpdateId, Revision = 1, ResultCode = InstallResultCode.Succeeded, RebootRequired = true },
                    new InstallResult { UpdateId = available[2].UpdateId, Revision = 1, ResultCode = InstallResultCode.Failed, HResult = unchecked((int)0x80070643) },
                ],
                RebootRequired = true,
            }, CancellationToken.None);

            var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
            await inventory.StoreAsync(ids[MainServer], new InventoryRequest
            {
                HostName = MainServer,
                DomainName = "lab.local",
                TimeZoneId = "W. Europe Standard Time",
                OperatingSystem = new OperatingSystemInfo { ProductName = "Windows Server 2022 Datacenter", EditionId = "ServerDatacenter", DisplayVersion = "21H2", Version = "10.0.20348.2762" },
                Roles = ["SqlServer"],
                UpdateSource = new UpdateSourceInfo { AutomaticUpdates = new AutomaticUpdatesInfo { Mode = AutomaticUpdateMode.AutoDownloadNotifyInstall, ManagedByAgent = true } },
                UpdateHistory =
                [
                    new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = Updates[0].Title, Date = DateTimeOffset.UtcNow.AddDays(-20), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded, ClientApplicationId = UpdateHistoryEntry.AgentClientApplicationId },
                    new UpdateHistoryEntry { UpdateId = Guid.NewGuid(), Revision = 1, Title = Updates[5].Title, Date = DateTimeOffset.UtcNow.AddDays(-19), Operation = UpdateOperation.Installation, ResultCode = InstallResultCode.Succeeded, ClientApplicationId = "AutomaticUpdates" },
                ],
            }, CancellationToken.None);

            var actions = scope.ServiceProvider.GetRequiredService<AgentActionService>();
            await actions.QueueAsync(ids[MainServer], AgentActionKind.SearchNow, "marc", CancellationToken.None);
        }

        // Live status of the main server: installing the cumulative update.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            var main = await db.Clients.FindAsync(ids[MainServer]);
            main!.ActivityPhase = AgentPhase.Installing;
            main.ActivityPercent = 42;
            main.ActivityUpdateIds = [available[0].UpdateId];
            main.ActivityStartedAtUtc = now.AddMinutes(-6);
            main.ActivityReportedAtUtc = now;

            // Two weeks of daily searches and inventories, so the activity list has more than one page.
            for (var day = 14; day >= 1; day--)
            {
                db.ClientActivities.Add(new ClientActivityEntity { ClientId = ids[MainServer], AtUtc = now.AddDays(-day), Kind = ClientActivityKind.Search, Detail = "Update search succeeded: 2 updates available." });
                db.ClientActivities.Add(new ClientActivityEntity { ClientId = ids[MainServer], AtUtc = now.AddDays(-day).AddMinutes(5), Kind = ClientActivityKind.Inventory, Detail = "Inventory received." });
            }

            // Security data of two monthly MSRC releases: every demo server is behind, one vulnerability is exploited.
            foreach (var (build, october, september) in new[] { (14393, 9612, 9339), (17763, 9376, 9245), (20348, 5761, 5622), (26100, 33650, 33438) })
            {
                db.SecurityBaselines.Add(new SecurityBaselineEntity { Build = build, Month = "2026-Oct", ReleasedAtUtc = now.AddDays(-13), Revision = october, Kb = "5126655" });
                db.SecurityBaselines.Add(new SecurityBaselineEntity { Build = build, Month = "2026-Sep", ReleasedAtUtc = now.AddDays(-48), Revision = september, Kb = "5122882" });
                db.SecurityFixes.Add(new SecurityFixEntity { Cve = "CVE-2026-96951", Month = "2026-Oct", Build = build, Revision = october, Kb = "5126655" });
                db.SecurityFixes.Add(new SecurityFixEntity { Cve = "CVE-2026-100201", Month = "2026-Oct", Build = build, Revision = october, Kb = "5126655" });
                db.SecurityFixes.Add(new SecurityFixEntity { Cve = "CVE-2026-69354", Month = "2026-Sep", Build = build, Revision = september, Kb = "5122882" });
            }

            db.SecurityVulnerabilities.Add(new SecurityVulnerabilityEntity { Cve = "CVE-2026-96951", Month = "2026-Oct", Title = "Windows Common Log File System Driver Elevation of Privilege Vulnerability", Severity = "Important", Impact = "Elevation of Privilege", CvssScore = 7.8, Exploited = true });
            db.SecurityVulnerabilities.Add(new SecurityVulnerabilityEntity { Cve = "CVE-2026-100201", Month = "2026-Oct", Title = "Windows Client License Service Elevation of Privilege Vulnerability", Severity = "Important", Impact = "Elevation of Privilege", CvssScore = 7.0 });
            db.SecurityVulnerabilities.Add(new SecurityVulnerabilityEntity { Cve = "CVE-2026-69354", Month = "2026-Sep", Title = "Windows TCP/IP Remote Code Execution Vulnerability", Severity = "Critical", Impact = "Remote Code Execution", CvssScore = 9.8, PubliclyDisclosed = true });

            await db.SaveChangesAsync();
        }

        return ids[MainServer];
    }
}
