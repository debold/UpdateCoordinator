using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Amsi;
using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Contracts.Scripts;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Components;
using SUSyphos.Server.Data;
using SUSyphos.Server.Scripts;
using SUSyphos.Server.Software;
using SUSyphos.Server.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>A central server whose virus scanner the test decides.</summary>
internal sealed class ScannedServerFactory : ServerFactory
{
    public ScanResult ScanResult { get; set; } = new(ScanOutcome.Clean, "No threat found.", 1);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IInstallerScanner>(new Scanner(this)));
    }

    private sealed class Scanner(ScannedServerFactory factory) : IInstallerScanner
    {
        public ScanResult Scan(string path, long maxBytes) => factory.ScanResult;

        public ScanResult ScanBuffer(byte[] data, string contentName) => factory.ScanResult;
    }
}

/// <summary>Signed scripts of WinGet packages on the central server (Marc, 2026-11-13/14).</summary>
public sealed class ScriptTests
{
    private const string Password = "correct horse battery";
    private const string Package = "Notepad++.Notepad++";

    private static async Task<ScannedServerFactory> CreateAsync()
    {
        var factory = new ScannedServerFactory();
        await factory.WithDbAsync(db =>
        {
            DemoSoftware.Add(db, [], [], DateTime.UtcNow);
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        return factory;
    }

    private static async Task<T> WithAsync<T>(ServerFactory factory, Func<ScriptAdministration, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ScriptAdministration>());
    }

    private static Task<Outcome> SaveAsync(ServerFactory factory, string content, IReadOnlyList<ScriptParameter>? parameters = null, ScriptPhase phase = ScriptPhase.BeforeInstall) =>
        WithAsync(factory, s => s.SaveDraftAsync(Package, phase, content, parameters ?? [], ScriptRunOn.Both, ScriptOnFailure.Stop, 300, "admin", CancellationToken.None));

    private static async Task<int> ScriptIdAsync(ServerFactory factory) =>
        await factory.WithDbAsync(db => db.PackageScripts.Select(s => s.Id).SingleAsync(TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(ScriptPhase.BeforeInstall, "", ScriptRunOn.Both, ScriptOnFailure.Stop, 300)]
    [InlineData(ScriptPhase.BeforeInstall, "exit 0", ScriptRunOn.Both, ScriptOnFailure.Stop, 30)]
    [InlineData(ScriptPhase.BeforeInstall, "exit 0", ScriptRunOn.Both, ScriptOnFailure.Stop, 3600)]
    [InlineData(ScriptPhase.AfterUninstall, "exit 0", ScriptRunOn.Upgrade, ScriptOnFailure.Stop, 300)]
    [InlineData(ScriptPhase.AfterInstall, "exit 0", ScriptRunOn.Both, ScriptOnFailure.Continue, 300)]
    public void InvalidDrafts_AreRefused(ScriptPhase phase, string content, ScriptRunOn runOn, ScriptOnFailure onFailure, int timeout) =>
        Assert.NotNull(ScriptAdministration.Validate(phase, content, [], runOn, onFailure, timeout));

    [Theory]
    [InlineData("1abc", false, null)]
    [InlineData("Token", true, "default")]
    [InlineData("has space", false, null)]
    public void InvalidParameters_AreRefused(string name, bool secret, string? defaultValue) =>
        Assert.NotNull(ScriptAdministration.Validate(ScriptPhase.BeforeInstall, "exit 0", [new ScriptParameter(name, secret, false, defaultValue, null)], ScriptRunOn.Both, ScriptOnFailure.Stop, 300));

    [Fact]
    public void ParametersTwice_AreRefused_AndValidOnesPass()
    {
        Assert.NotNull(ScriptAdministration.Validate(ScriptPhase.BeforeInstall, "exit 0", [new("A", false, false, null, null), new("a", false, false, null, null)], ScriptRunOn.Both, ScriptOnFailure.Stop, 300));
        Assert.Null(ScriptAdministration.Validate(ScriptPhase.BeforeInstall, "exit 0", [new("Port", false, true, "8080", "listening port"), new("Token", true, true, null, null)], ScriptRunOn.Install, ScriptOnFailure.Continue, 600));
    }

    [Fact]
    public async Task Approval_SignsARevision_ThatVerifiesAgainstTheTrustAnchor()
    {
        await using var factory = await CreateAsync();
        Assert.True((await SaveAsync(factory, "Write-Output 'Grüße'\nexit 0")).Succeeded);
        var id = await ScriptIdAsync(factory);

        Assert.False((await WithAsync(factory, s => s.ApproveAsync(id, false, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None))).Succeeded);

        var package = await WithAsync(factory, s => s.PackageAsync(id, 1, CancellationToken.None));
        var revision = await factory.WithDbAsync(db => db.PackageScriptRevisions.SingleAsync(TestContext.Current.CancellationToken));
        var reference = new ScriptReference(ScriptPhase.BeforeInstall, id, 1, revision.ContentSha256, revision.ParametersSha256);
        await using var scope = factory.Services.CreateAsyncScope();
        var anchor = scope.ServiceProvider.GetRequiredService<TrustAnchor>().PublicKey;
        Assert.Null(ScriptSigningFormat.Verify(package!, anchor, reference, Package));
        Assert.Equal([0xEF, 0xBB, 0xBF], revision.Content[..3]);
        Assert.Contains("\r\n", ScriptSigningFormat.ContentText(revision.Content), StringComparison.Ordinal);

        // The draft became the revision; a second approval needs a new draft.
        Assert.Null((await factory.WithDbAsync(db => db.PackageScripts.SingleAsync(TestContext.Current.CancellationToken))).DraftContent);
        Assert.False((await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None))).Succeeded);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Script.Approved", TestContext.Current.CancellationToken)));
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Script.KeyCreated", TestContext.Current.CancellationToken)));

        // A changed database entry no longer verifies.
        await factory.WithDbAsync(db => db.PackageScriptRevisions.ExecuteUpdateAsync(s => s.SetProperty(r => r.TimeoutSeconds, 1800), TestContext.Current.CancellationToken));
        var changed = await WithAsync(factory, s => s.PackageAsync(id, 1, CancellationToken.None));
        Assert.NotNull(ScriptSigningFormat.Verify(changed!, anchor, reference, Package));
    }

    [Fact]
    public async Task ScanFinding_RefusesTheApproval()
    {
        await using var factory = await CreateAsync();
        factory.ScanResult = new ScanResult(ScanOutcome.Detected, "Trojan:PowerShell/Test", 32768);
        Assert.True((await SaveAsync(factory, "exit 0")).Succeeded);

        var outcome = await WithAsync(factory, async s => await s.ApproveAsync(await ScriptIdAsync(factory), true, "admin", CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("Trojan", outcome.Error, StringComparison.Ordinal);
        Assert.False(await factory.WithDbAsync(db => db.PackageScriptRevisions.AnyAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task References_AreEmptyWhileSwitchedOff_AndCarryAssignmentValuesAndSecrets()
    {
        await using var factory = await CreateAsync();
        var server = await RegisterAsync(factory);
        Assert.True((await SaveAsync(factory, "exit 0", [new("Port", false, true, "8080", null), new("Token", true, true, null, null)])).Succeeded);
        var id = await ScriptIdAsync(factory);
        Assert.True((await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None))).Succeeded);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<SoftwareAssignments>().AddAsync(Package, null, server, null, false, "admin", CancellationToken.None)).Succeeded);
        }

        var assignment = await factory.WithDbAsync(db => db.PackageAssignments.Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await WithAsync(factory, s => s.ReferencesAsync(Package, server, [ScriptPhase.BeforeInstall], CancellationToken.None)));

        Assert.True((await WithAsync(factory, s => s.SetEnabledAsync(true, "admin", CancellationToken.None))).Succeeded);
        Assert.Equal("8080", Assert.Single(await WithAsync(factory, s => s.ReferencesAsync(Package, server, [ScriptPhase.BeforeInstall], CancellationToken.None))).Values!["Port"]);

        Assert.True((await WithAsync(factory, s => s.SetValuesAsync(assignment, new Dictionary<(int, string), string?> { [(id, "Port")] = "9090", [(id, "Token")] = "s3cret!" }, "admin", CancellationToken.None))).Succeeded);
        var stored = await factory.WithDbAsync(db => db.PackageAssignmentScriptValues.SingleAsync(v => v.Name == "Token", TestContext.Current.CancellationToken));
        Assert.Null(stored.Value);
        Assert.NotNull(stored.ProtectedValue);

        // An empty secret keeps the stored one.
        Assert.True((await WithAsync(factory, s => s.SetValuesAsync(assignment, new Dictionary<(int, string), string?> { [(id, "Token")] = string.Empty }, "admin", CancellationToken.None))).Succeeded);
        var reference = Assert.Single(await WithAsync(factory, s => s.ReferencesAsync(Package, server, [ScriptPhase.BeforeInstall], CancellationToken.None)));
        Assert.Equal(("9090", "s3cret!"), (reference.Values!["Port"], reference.Values["Token"]));
        Assert.DoesNotContain(await factory.WithDbAsync(db => db.AuditEntries.Select(e => e.Details).ToListAsync(TestContext.Current.CancellationToken)), d => d != null && d.Contains("s3cret!", StringComparison.Ordinal));

        Assert.False((await WithAsync(factory, s => s.SetValuesAsync(assignment, new Dictionary<(int, string), string?> { [(id, "Unknown")] = "x" }, "admin", CancellationToken.None))).Succeeded);
        Assert.Empty(await WithAsync(factory, s => s.ReferencesAsync(Package, server, [ScriptPhase.AfterUninstall], CancellationToken.None)));
    }


    private static byte[] FilesZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Files_AreSignedWithTheRevision_AndHandedToAgentsOnly()
    {
        // Marc, 2026-11-22: a ZIP of files per revision, for example the .bgi file of BGInfo.
        await using var factory = await CreateAsync();
        var http = factory.CreateClient();
        var register = await RegisterFullAsync(factory, http);
        Assert.False((await WithAsync(factory, s => s.SetDraftFilesAsync(Package, ScriptPhase.BeforeInstall, new MemoryStream(FilesZip(("config.bgi", "x"))), "bginfo.zip", "admin", CancellationToken.None))).Succeeded);
        Assert.True((await SaveAsync(factory, "Copy-Item (Join-Path $env:SUSYPHOS_FILES config.bgi) C:\\ProgramData\\BGInfo\nexit 0")).Succeeded);
        var id = await ScriptIdAsync(factory);

        // A path out of the folder or a finding of the virus scan is refused.
        Assert.False((await WithAsync(factory, s => s.SetDraftFilesAsync(Package, ScriptPhase.BeforeInstall, new MemoryStream(FilesZip(("../evil.ps1", "x"))), "evil.zip", "admin", CancellationToken.None))).Succeeded);
        factory.ScanResult = new ScanResult(ScanOutcome.Detected, "AMSI result 32768", 32768);
        Assert.False((await WithAsync(factory, s => s.SetDraftFilesAsync(Package, ScriptPhase.BeforeInstall, new MemoryStream(FilesZip(("config.bgi", "x"))), "bginfo.zip", "admin", CancellationToken.None))).Succeeded);
        factory.ScanResult = new ScanResult(ScanOutcome.Clean, "No threat found.", 1);
        Assert.Equal(0, await factory.WithDbAsync(db => db.ScriptFiles.CountAsync(TestContext.Current.CancellationToken)));

        var zip = FilesZip(("config.bgi", "BGINFO"), ("logo.bmp", "BM"));
        Assert.True((await WithAsync(factory, s => s.SetDraftFilesAsync(Package, ScriptPhase.BeforeInstall, new MemoryStream(zip), "bginfo.zip", "admin", CancellationToken.None))).Succeeded);
        Assert.True((await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None))).Succeeded);

        var package = await WithAsync(factory, s => s.PackageAsync(id, 1, CancellationToken.None));
        var revision = await factory.WithDbAsync(db => db.PackageScriptRevisions.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ScriptSigningFormat.Sha256(zip), package!.FilesSha256);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var anchor = scope.ServiceProvider.GetRequiredService<TrustAnchor>().PublicKey;
            var reference = new ScriptReference(ScriptPhase.BeforeInstall, id, 1, revision.ContentSha256, revision.ParametersSha256);
            Assert.Null(ScriptSigningFormat.Verify(package, anchor, reference, Package));
            // Other files under the same signature (or none, as an agent before rc.8 would read it) do not verify.
            Assert.NotNull(ScriptSigningFormat.Verify(package with { FilesSha256 = new string('A', 64) }, anchor, reference, Package));
            Assert.NotNull(ScriptSigningFormat.Verify(package with { FilesSha256 = null }, anchor, reference, Package));
        }

        // Agents get the ZIP while scripts are on; without credentials nothing.
        Assert.True((await WithAsync(factory, s => s.SetEnabledAsync(true, "admin", CancellationToken.None))).Succeeded);
        using (var response = await SendAsync(http, register, HttpMethod.Get, ApiRoutes.ScriptFilesFor(id, 1)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(zip, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(new Uri(ApiRoutes.ScriptFilesFor(id, 1), UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode);

        // Removing the files from the draft keeps those of the approved revision until the next approval.
        Assert.True((await WithAsync(factory, s => s.RemoveDraftFilesAsync(Package, ScriptPhase.BeforeInstall, "admin", CancellationToken.None))).Succeeded);
        Assert.Equal(1, await factory.WithDbAsync(db => db.ScriptFiles.CountAsync(TestContext.Current.CancellationToken)));
        Assert.True((await WithAsync(factory, s => s.DiscardDraftAsync(id, "admin", CancellationToken.None))).Succeeded);
        Assert.Equal(ScriptSigningFormat.Sha256(zip), await factory.WithDbAsync(db => db.PackageScripts.Select(s => s.DraftFilesSha256).SingleAsync(TestContext.Current.CancellationToken)));

        // The script page lists the files for every role.
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var page = await (await reader.GetAsync(ScriptText.Link(Package, ScriptPhase.BeforeInstall))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("config.bgi", page, StringComparison.Ordinal);
        Assert.Contains(ScriptEndpoints.FilesDownloadPath(ScriptSigningFormat.Sha256(zip)), page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"FilesZip\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_AreUploadedThroughTheScriptPage_ByAdministrators()
    {
        await using var factory = await CreateAsync();
        Assert.True((await SaveAsync(factory, "exit 0")).Succeeded);
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var path = ScriptText.Link(Package, ScriptPhase.BeforeInstall);
        var page = await (await admin.GetAsync(path)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("enctype=\"multipart/form-data\"", page, StringComparison.Ordinal);
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        if (token.Length == 0)
        {
            token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("script-files"), "_handler" },
        };
        var file = new ByteArrayContent(FilesZip(("config.bgi", "BGINFO")));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        form.Add(file, "FilesZip", "bginfo.zip");
        using var posted = await admin.Http.PostAsync(new Uri(path, UriKind.Relative), form, TestContext.Current.CancellationToken);
        var result = await posted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("The files were added to the draft", result, StringComparison.Ordinal);
        Assert.Equal("bginfo.zip", await factory.WithDbAsync(db => db.ScriptFiles.Select(f => f.FileName).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SyntaxErrors_AreShown_AndBlockTheApproval()
    {
        // Marc, 2026-11-22: the real parser of PowerShell checks the draft; saving works, approving does not.
        await using var factory = await CreateAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var parser = scope.ServiceProvider.GetRequiredService<IScriptParser>();
            var good = await parser.ParseAsync("param()\nWrite-Output 'ok'\nexit 0", ScriptPowerShell.WindowsPowerShell, CancellationToken.None);
            var bad = await parser.ParseAsync("Write-Output 'ok'\nif ($true {\n", ScriptPowerShell.WindowsPowerShell, CancellationToken.None);
            Assert.True(good.Checked, good.Problem);
            Assert.True(good.Ok);
            Assert.True(bad.Checked, bad.Problem);
            Assert.False(bad.Ok);
            Assert.Equal(2, bad.Errors[0].Line);
        }

        Assert.True((await SaveAsync(factory, "if ($true {\nexit 0")).Succeeded);
        var id = await ScriptIdAsync(factory);
        var refused = await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None));
        Assert.False(refused.Succeeded);
        Assert.Contains("syntax error", refused.Error, StringComparison.Ordinal);

        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        var page = await (await admin.GetAsync(ScriptText.Link(Package, ScriptPhase.BeforeInstall))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("syntax error", page, StringComparison.Ordinal);
        Assert.Contains("Line 1", page, StringComparison.Ordinal);
        Assert.Contains("lib/prism/prism-powershell", page, StringComparison.Ordinal);

        // The check while typing answers administrators only.
        using var response = await admin.Http.PostAsJsonAsync(new Uri(ScriptEndpoints.ParsePath, UriKind.Relative), new { content = "exit 0", powerShell = "WindowsPowerShell" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ok\":true", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        using var forbidden = await reader.Http.PostAsJsonAsync(new Uri(ScriptEndpoints.ParsePath, UriKind.Relative), new { content = "exit 0", powerShell = "WindowsPowerShell" }, TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, forbidden.StatusCode);
    }
    [Fact]
    public async Task Agent_GetsTheScriptOnlyWhileSwitchedOn_AndReportsRunsOnce()
    {
        await using var factory = await CreateAsync();
        var http = factory.CreateClient();
        var register = await RegisterFullAsync(factory, http);
        Assert.True((await SaveAsync(factory, "exit 0")).Succeeded);
        var id = await ScriptIdAsync(factory);
        Assert.True((await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None))).Succeeded);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(http, register, HttpMethod.Get, ApiRoutes.ScriptFor(id, 1))).StatusCode);
        Assert.True((await WithAsync(factory, s => s.SetEnabledAsync(true, "admin", CancellationToken.None))).Succeeded);
        using (var response = await SendAsync(http, register, HttpMethod.Get, ApiRoutes.ScriptFor(id, 1)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Package, (await response.Content.ReadFromJsonAsync<ScriptPackage>(TestContext.Current.CancellationToken))!.PackageId);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(http, register, HttpMethod.Get, ApiRoutes.ScriptFor(id, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(new Uri(ApiRoutes.ScriptFor(id, 1), UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode);

        var run = new ScriptRunReport
        {
            RunId = Guid.NewGuid(),
            PackageId = Package,
            Phase = ScriptPhase.BeforeInstall,
            Action = ScriptAction.Install,
            ScriptId = id,
            Revision = 1,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-3),
            FinishedAt = DateTimeOffset.UtcNow,
            ExitCode = 1,
            Result = ScriptRunResult.Failed,
            Output = "2026-11-15T10:00:00.000Z [Output] start\n2026-11-15T10:00:01.000Z [Error] boom (line 2)\n2026-11-15T10:00:01.000Z [Warning] careful",
        };
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(http, register, HttpMethod.Post, ApiRoutes.AgentScriptRuns, run)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(http, register, HttpMethod.Post, ApiRoutes.AgentScriptRuns, run)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(http, register, HttpMethod.Post, ApiRoutes.AgentScriptRuns, run with { RunId = Guid.NewGuid(), FinishedAt = run.StartedAt.AddDays(-1) })).StatusCode);

        var stored = await factory.WithDbAsync(db => db.ScriptRuns.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal((3, 1, 1), (stored.Lines, stored.Errors, stored.Warnings));

        // Every role reads the output; the page shows the lines, the filter by stream and the .log download.
        await factory.CreateUserAsync("reader", Password, AdminRoles.Reader);
        using var reader = new DashboardClient(factory);
        using (await reader.LoginAsync("reader", Password))
        {
        }

        var page = await (await reader.GetAsync($"{ScriptText.RunLink(register.ClientId, stored.Id)}?stream=Error")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("boom (line 2)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("careful", page, StringComparison.Ordinal);
        using var log = await reader.GetAsync(ScriptRunEndpoints.DownloadPath(register.ClientId, stored.Id));
        Assert.Contains("careful", await log.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // The history entry of the installation links the output of the script that ran with it (Marc, 2026-11-22).
        var software = Guid.NewGuid();
        await factory.WithDbAsync(async db =>
        {
            db.Updates.Add(new UpdateEntity { UpdateId = software, Revision = 1, Title = $"{Package} 1.0", Kind = UpdateKind.Software, PackageId = Package, FirstSeenAtUtc = DateTime.UtcNow });
            db.Installations.Add(new InstallationEntity { ClientId = register.ClientId, RunId = Guid.NewGuid(), UpdateId = software, Revision = 1, ResultCode = (int)SUSyphos.Contracts.Agents.InstallResultCode.Succeeded, StartedAtUtc = run.StartedAt.UtcDateTime, FinishedAtUtc = run.FinishedAt.UtcDateTime.AddMinutes(1), Source = "SUSyphos" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        var history = System.Net.WebUtility.HtmlDecode(await (await reader.GetAsync($"/servers/{register.ClientId}?tab=history")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains($"href=\"{ScriptText.RunLink(register.ClientId, stored.Id)}\" data-help-term=\"Script output\">Before installation output", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptPage_AdministratorsSaveAndApprove_OthersOnlyRead()
    {
        await using var factory = await CreateAsync();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        var link = ScriptText.Link(Package, ScriptPhase.AfterInstall);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        Assert.Contains("Save draft", await (await admin.GetAsync(link)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (await admin.PostFormAsync(link, new Dictionary<string, string> { ["_handler"] = "script-edit", ["Content"] = "Write-Output (Get-SUSyphosParameter Port)\nexit 3010", ["RunOn"] = "Upgrade", ["OnFailure"] = "Stop", ["TimeoutMinutes"] = "10", ["Param0Name"] = "Port", ["Param0Type"] = "text", ["Param0Default"] = "8080" }))
        {
        }

        var script = await factory.WithDbAsync(db => db.PackageScripts.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal((ScriptPhase.AfterInstall, ScriptRunOn.Upgrade, 600), (script.Phase, script.DraftRunOn, script.DraftTimeoutSeconds));
        Assert.Equal("Port", Assert.Single(ScriptSigningFormat.ReadParameters(script.DraftParametersJson!)).Name);

        Assert.Contains("I have read the whole script", await (await admin.GetAsync($"{link}?dialog=approve")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (await admin.PostFormAsync($"{link}?dialog=approve", new Dictionary<string, string> { ["_handler"] = "script-approve", ["ReadWholeScript"] = "true" }))
        {
        }

        Assert.True(await factory.WithDbAsync(db => db.PackageScriptRevisions.AnyAsync(TestContext.Current.CancellationToken)));
        using (var download = await admin.GetAsync(ScriptEndpoints.DownloadPath(Package, ScriptPhase.AfterInstall, 1)))
        {
            Assert.Contains("exit 3010", await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var packagePage = await (await admin.GetAsync(PackagePage.Link(Package))).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("revision 1", packagePage, StringComparison.Ordinal);

        using var op = new DashboardClient(factory);
        using (await op.LoginAsync("operator", Password))
        {
        }

        var read = await (await op.GetAsync(link)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("exit 3010", read, StringComparison.Ordinal);
        using (await op.PostFormAsync(link, new Dictionary<string, string> { ["_handler"] = "script-edit", ["Content"] = "Remove-Item C:\\ -Recurse", ["TimeoutMinutes"] = "5" }))
        {
        }

        Assert.Null((await factory.WithDbAsync(db => db.PackageScripts.SingleAsync(TestContext.Current.CancellationToken))).DraftContent);
    }

    [Fact]
    public async Task AgentSettings_SwitchScriptsOnAndOff_Audited()
    {
        await using var factory = await CreateAsync();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        Assert.Contains("Scripts for software", await (await admin.GetAsync("/administration/agent-settings")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        using (await admin.PostFormAsync("/administration/agent-settings", new Dictionary<string, string> { ["_handler"] = "scripts-switch", ["ScriptsAction"] = "on" }))
        {
        }

        Assert.True(await WithAsync(factory, s => s.IsEnabledAsync(CancellationToken.None)));
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Script.Enabled", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ReleaseList_CarriesScripts_AndTheInstalledVersion()
    {
        await using var factory = await CreateAsync();
        var client = await RegisterAsync(factory);
        var updateId = SoftwarePackages.SoftwareUpdateId(Package, "8.9.9");
        await factory.WithDbAsync(db =>
        {
            db.ClientSoftware.Add(new ClientSoftwareEntity { ClientId = client, View = SoftwareView.Machine64, KeyName = "Notepad++", DisplayName = "Notepad++", PackageId = Package, InstalledVersion = "8.9.8", MatchKind = SoftwareMatch.WinGet, ReportedAtUtc = DateTime.UtcNow });
            db.Releases.Add(new ReleaseEntity { ClientId = client, UpdateId = updateId, Revision = SoftwarePackages.SoftwareRevision, EarliestInstallAtUtc = DateTime.UtcNow });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        Assert.True((await SaveAsync(factory, "exit 0")).Succeeded);
        var id = await ScriptIdAsync(factory);
        Assert.True((await WithAsync(factory, s => s.SetEnabledAsync(true, "admin", CancellationToken.None))).Succeeded);
        Assert.True((await WithAsync(factory, s => s.ApproveAsync(id, true, "admin", CancellationToken.None))).Succeeded);

        await using var scope = factory.Services.CreateAsyncScope();
        var releases = await scope.ServiceProvider.GetRequiredService<SUSyphos.Server.Agents.UpdateReportService>().GetReleasesAsync(client, CancellationToken.None);

        var software = Assert.Single(releases.Releases).Software!;
        Assert.Equal("8.9.8", software.InstalledVersion);
        Assert.Equal((ScriptPhase.BeforeInstall, id, 1), (Assert.Single(software.Scripts!).Phase, software.Scripts![0].ScriptId, software.Scripts[0].Revision));
    }

    private static async Task<Guid> RegisterAsync(ServerFactory factory)
    {
        using var http = factory.CreateClient();
        return (await RegisterFullAsync(factory, http)).ClientId;
    }

    private static async Task<RegisterResponse> RegisterFullAsync(ServerFactory factory, HttpClient http)
    {
        using var response = await http.PostAsJsonAsync(new Uri(ApiRoutes.AgentRegister, UriKind.Relative), new RegisterRequest { HostName = "APP01", OsVersion = "10.0.20348.1", AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken);
        var register = (await response.Content.ReadFromJsonAsync<RegisterResponse>(TestContext.Current.CancellationToken))!;
        await factory.WithDbAsync(db => db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.State, RegistrationState.Approved).SetProperty(c => c.SoftwareState, SoftwareSupportState.Supported), TestContext.Current.CancellationToken));
        return register;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, RegisterResponse register, HttpMethod method, string route, object? body = null)
    {
        using var message = new HttpRequestMessage(method, new Uri(route, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        message.Headers.Authorization = AgentCredentials.ToHeader(register.ClientId, register.ClientSecret);
        return await http.SendAsync(message, TestContext.Current.CancellationToken);
    }
}
