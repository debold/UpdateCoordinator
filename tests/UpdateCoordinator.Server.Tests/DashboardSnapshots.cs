using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.Server.Tests;

/// <summary>
/// Development aid, not a check: writes the rendered dashboard pages with example data as standalone HTML files for a
/// visual review. Runs only when UPDATECOORDINATOR_SNAPSHOTS names an output folder.
/// </summary>
public sealed partial class DashboardSnapshots
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WritePages()
    {
        var output = Environment.GetEnvironmentVariable("UPDATECOORDINATOR_SNAPSHOTS");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Set UPDATECOORDINATOR_SNAPSHOTS to an output folder to write dashboard snapshots.");
        Directory.CreateDirectory(output!);

        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("marc", Password, AdminRoles.Admin);
        await SeedAsync(factory);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("marc", Password))
        {
        }

        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "UpdateCoordinator.Server", "wwwroot", "app.css"), TestContext.Current.CancellationToken);
        var pages = (Environment.GetEnvironmentVariable("UPDATECOORDINATOR_SNAPSHOT_PAGES") ?? "/;/servers;/servers?group=os;/servers/{sql01};/servers/{sql01}?dialog=remove")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var sql01 = await factory.WithDbAsync(db => db.Clients.Where(c => c.HostName == "SQL01").Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));
        var index = 0;
        foreach (var page in pages)
        {
            using var response = await browser.GetAsync(page.Replace("{sql01}", sql01.ToString(), StringComparison.Ordinal));
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            html = StylesheetRegex().Replace(html, $"<style>{css}</style>");
            await File.WriteAllTextAsync(Path.Combine(output!, $"page-{index++:00}.html"), $"<!-- {page} -->\n{html}", TestContext.Current.CancellationToken);
        }
    }

    private static Task<int> SeedAsync(ServerFactory factory) =>
        factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Groups.Add(new GroupEntity { Id = 10, Name = "SQL cluster" });
            db.Groups.Add(new GroupEntity { Id = 11, Name = "Web farm" });
            var servers = new (string Host, string Domain, string Version, int Ring, RegistrationState State, int MinutesAgo, int? Group, bool Reboot)[]
            {
                ("LABVDC01", "lab.local", "10.0.20348.2762", RingEntity.PilotId, RegistrationState.Approved, 2, null, false),
                ("LABVDC02", "lab.local", "10.0.20348.2762", RingEntity.TestId, RegistrationState.Approved, 1, null, false),
                ("SQL01", "lab.local", "10.0.17763.6414", RingEntity.ProductionId, RegistrationState.Approved, 1, 10, false),
                ("SQL02", "lab.local", "10.0.17763.6414", RingEntity.ProductionId, RegistrationState.Approved, 4, 10, true),
                ("FS01", "lab.local", "10.0.14393.7428", RingEntity.ProductionId, RegistrationState.Approved, 3, null, false),
                ("APP01", "lab.local", "10.0.26100.2033", RingEntity.TestId, RegistrationState.Approved, 1, null, false),
                ("WEB01", "dmz.local", "10.0.20348.2700", RingEntity.ProductionId, RegistrationState.Approved, 2900, 11, false),
                ("WEB02", "dmz.local", "10.0.20348.2762", RingEntity.ProductionId, RegistrationState.Approved, 2, 11, false),
                ("NEWSRV01", "lab.local", "10.0.20348.2762", RingEntity.ProductionId, RegistrationState.Pending, 5, null, false),
            };
            foreach (var s in servers)
            {
                var id = Guid.NewGuid();
                db.Clients.Add(new ClientEntity
                {
                    Id = id, SecretHash = [1], HostName = s.Host, DomainName = s.Domain, OsVersion = s.Version, AgentVersion = "0.2.0",
                    State = s.State, RingId = s.Ring, LastHeartbeatAtUtc = now.AddMinutes(-s.MinutesAgo), RegisteredAtUtc = now.AddDays(-3),
                    OsProductName = "Windows Server Datacenter", RebootPending = s.Reboot,
                });
                if (s.Group is { } group)
                {
                    db.ClientGroups.Add(new ClientGroupEntity { ClientId = id, GroupId = group, Manual = true });
                }
            }

            return await db.SaveChangesAsync();
        });

    [GeneratedRegex("<link rel=\"stylesheet\"[^>]*>")]
    private static partial Regex StylesheetRegex();
}
