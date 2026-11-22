using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Scripts;
using SUSyphos.Agent.Trust;
using SUSyphos.Common;
using SUSyphos.Contracts.Scripts;
using SUSyphos.Contracts.Trust;

namespace SUSyphos.Agent.Tests;

/// <summary>Signed scripts on the agent (Marc, 2026-11-13): nothing runs that does not verify, every run is reported.</summary>
public sealed class ScriptRunnerTests : IDisposable
{
    private const string Package = "Contoso.Viewer";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
    private readonly ECDsa _anchor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeScriptClient _client = new();
    private readonly FakeScriptProcess _process = new();

    public ScriptRunnerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _anchor.Dispose();
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task VerifiedScript_Runs_IsReported_AndSecretsAreMasked()
    {
        var (package, reference) = Sign("Write-Output 'hello'", revision: 2, parameters: [new ScriptParameter("Password", true, true, null, null)], values: new Dictionary<string, string> { ["Password"] = "s3cret!" });
        _client.Packages[(1, 2)] = package;
        _process.Output = "2026-11-15T10:00:00.000Z [Output] using s3cret! now";

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(ScriptRunResult.Succeeded, outcome.Result);
        var report = Assert.Single(_client.Runs);
        Assert.Contains(ScriptLimits.Mask, report.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret!", report.Output, StringComparison.Ordinal);
        Assert.Equal("{\"Password\":\"s3cret!\"}", _process.Parameters);
        Assert.Equal("Install", _process.Environment!["SUSYPHOS_ACTION"]);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_folder, "Scripts")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder, "Logs", "Scripts")));
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PowerShell7Revision_RunsIn7_OrSaysItFellBackTo51(bool installed)
    {
        // Marc, 2026-11-20: PowerShell 7 as an option per script, falling back to Windows PowerShell 5.1.
        var (package, reference) = Sign("Write-Output 'hello'", revision: 3, powerShell: ScriptPowerShell.PowerShell7);
        _client.Packages[(1, 3)] = package;
        _process.ShellFor7 = installed ? PowerShellScriptProcess.PowerShell7 : PowerShellScriptProcess.WindowsPowerShell;

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.True(_process.AskedFor7);
        Assert.Equal(!installed, (Assert.Single(_client.Runs).Output ?? string.Empty).Contains("PowerShell 7 is not installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevisionChangedFrom51To7AfterSigning_NeverRuns()
    {
        // The PowerShell choice is part of the signature: a 5.1 signature does not cover a revision that asks for 7.
        var (package, reference) = Sign("Write-Output 'hello'", revision: 4, powerShell: ScriptPowerShell.PowerShell7, signAs51: true);
        _client.Packages[(1, 4)] = package;

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.Equal(ScriptRunResult.NotVerified, outcome.Result);
        Assert.Equal(0, _process.Calls);
    }
    [Theory]
    [InlineData("content")]
    [InlineData("package")]
    [InlineData("phase")]
    [InlineData("key")]
    [InlineData("signature")]
    [InlineData("missing")]
    public async Task TamperedOrForeignScripts_NeverRun(string tampering)
    {
        var (package, reference) = Sign("Write-Output 'hello'");
        switch (tampering)
        {
            case "content":
                package = package with { Content = Convert.ToBase64String(ScriptSigningFormat.NormalizeContent("Remove-Item C:\\ -Recurse")) };
                break;
            case "package":
                package = package with { PackageId = "Other.Package" };
                break;
            case "phase":
                reference = reference with { Phase = ScriptPhase.AfterInstall };
                break;
            case "key":
                using (var foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    package = package with { KeyCertificate = Convert.ToBase64String(foreign.SignData(ScriptSigningFormat.KeyCertificateBytes(package.KeyId, Convert.FromBase64String(package.KeyPublicKey), package.KeyCreatedAt), TrustBindingFormat.HashAlgorithm)) };
                }

                break;
            case "signature":
                package = package with { TimeoutSeconds = 1800 };
                break;
        }

        if (tampering != "missing")
        {
            _client.Packages[(1, 1)] = package;
        }

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ScriptRunResult.NotVerified, outcome.Result);
        Assert.Equal(0, _process.Calls);
        Assert.Equal(ScriptRunResult.NotVerified, Assert.Single(_client.Runs).Result);
    }

    [Fact]
    public async Task OlderRevision_NeverRunsAgain()
    {
        var (newer, newerReference) = Sign("Write-Output 'two'", revision: 2);
        var (older, olderReference) = Sign("Write-Output 'one'", revision: 1);
        _client.Packages[(1, 2)] = newer;
        _client.Packages[(1, 1)] = older;
        var runner = Create();

        Assert.True((await runner.RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, newerReference), TestContext.Current.CancellationToken)).Succeeded);
        var replay = await runner.RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, olderReference), TestContext.Current.CancellationToken);

        Assert.Equal(ScriptRunResult.NotVerified, replay.Result);
        Assert.Equal(1, _process.Calls);
    }

    [Fact]
    public async Task LocalVeto_BlocksEveryScript()
    {
        var (package, reference) = Sign("Write-Output 'hello'");
        _client.Packages[(1, 1)] = package;

        var outcome = await Create(blocked: true).RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.Equal(ScriptRunResult.Blocked, outcome.Result);
        Assert.Equal(0, _process.Calls);
    }

    [Theory]
    [InlineData(0, (int)ScriptRunResult.Succeeded, false)]
    [InlineData(3010, (int)ScriptRunResult.SucceededRestart, true)]
    [InlineData(1641, (int)ScriptRunResult.SucceededRestart, true)]
    [InlineData(1, (int)ScriptRunResult.Failed, false)]
    [InlineData(null, (int)ScriptRunResult.TimedOut, false)]
    public async Task ExitCodes_GiveTheirResult(int? exitCode, int expected, bool restart)
    {
        var (package, reference) = Sign("exit 0");
        _client.Packages[(1, 1)] = package;
        _process.ExitCode = exitCode;

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.Equal((ScriptRunResult)expected, outcome.Result);
        Assert.Equal(restart, outcome.RestartRequired);
        Assert.Equal(exitCode ?? ScriptLimits.TimeoutCode, Assert.Single(_client.Runs).ExitCode);
    }

    [Fact]
    public async Task ScriptForInstallationsOnly_IsSkippedOnAnUpgrade()
    {
        var (package, reference) = Sign("Write-Output 'new'", runOn: ScriptRunOn.Install);
        _client.Packages[(1, 1)] = package;

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Upgrade, "2.0", "1.0", null, reference), TestContext.Current.CancellationToken);

        Assert.False(outcome.Ran);
        Assert.True(outcome.Succeeded);
        Assert.Empty(_client.Runs);
    }

    [Fact]
    public async Task RealPowerShell_LogsEveryStream_WithUmlauts_AndTheExitCode()
    {
        // Runs Windows PowerShell 5.1 as the current user in a temp folder; changes nothing on the machine.
        var script = """
            Write-Output 'Grüße aus dem Skript'
            Write-Warning 'eine Warnung'
            Write-Verbose 'ausführlich'
            Write-Host 'auf den Host'
            Write-Error 'ein Fehler'
            Write-Output ("Wert: " + (Get-SUSyphosParameter Name))
            exit 3010
            """;
        var (package, reference) = Sign(script, parameters: [new ScriptParameter("Name", false, false, null, null)], values: new Dictionary<string, string> { ["Name"] = "Müller" });
        _client.Packages[(1, 1)] = package;
        var runner = new ScriptRunner(_client, new PowerShellScriptProcess(), Identity(), Trust(), State(), _time, NullLogger<ScriptRunner>.Instance, _folder)
        {
            IsBlockedLocally = () => false,
            PowerShellPolicyBlocker = () => null,
        };

        var outcome = await runner.RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.True(outcome.Result == ScriptRunResult.SucceededRestart, $"{outcome.Result}: {outcome.Detail} {_client.Runs.LastOrDefault()?.Output}");
        var lines = ScriptOutput.Parse(Assert.Single(_client.Runs).Output).ToList();
        Assert.Contains(lines, l => l.Stream == "Output" && l.Text == "Grüße aus dem Skript");
        Assert.Contains(lines, l => l.Stream == "Warning" && l.Text == "eine Warnung");
        Assert.Contains(lines, l => l.Stream == "Verbose" && l.Text == "ausführlich");
        Assert.Contains(lines, l => l.Stream == "Host" && l.Text == "auf den Host");
        Assert.Contains(lines, l => l.Stream == "Error" && l.Text.StartsWith("ein Fehler (", StringComparison.Ordinal) && l.Text.Contains("script.ps1", StringComparison.OrdinalIgnoreCase) && l.Text.Contains(" 5)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Stream == "Output" && l.Text == "Wert: Müller");
        Assert.Contains(lines, l => l.Stream == "Runner" && l.Text.StartsWith("Exit code 3010", StringComparison.Ordinal));
        Assert.True(lines.Any(l => l.Stream == "Runner" && l.Text.StartsWith("PowerShell 5.1", StringComparison.Ordinal)), string.Join(" ", _client.Runs[0].Output!.Take(120).Select(c => ((int)c).ToString("X", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task RealPowerShell_Timeout_EndsTheWholeProcessTree()
    {
        var script = "Start-Process -FilePath ping.exe -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden; Start-Sleep -Seconds 60";
        var (package, reference) = Sign(script, timeoutSeconds: 3);
        _client.Packages[(1, 1)] = package;
        var before = System.Diagnostics.Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
        var runner = new ScriptRunner(_client, new PowerShellScriptProcess(), Identity(), Trust(), State(), _time, NullLogger<ScriptRunner>.Instance, _folder)
        {
            IsBlockedLocally = () => false,
            PowerShellPolicyBlocker = () => null,
        };

        var outcome = await runner.RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.Equal(ScriptRunResult.TimedOut, outcome.Result);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(System.Diagnostics.Process.GetProcessesByName("PING"), p => !before.Contains(p.Id));
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
    public async Task SignedFiles_AreUnpackedForTheRun_CachedByHash_AndGoneAfterwards()
    {
        // Marc, 2026-11-22: the script finds its files in $env:SUSYPHOS_FILES.
        var zip = FilesZip(("config.bgi", "BGINFO"), ("sub/logo.bmp", "BM"));
        var (package, reference) = Sign("exit 0", revision: 4, filesSha256: ScriptSigningFormat.Sha256(zip));
        _client.Packages[(1, 4)] = package;
        _client.Files[(1, 4)] = zip;

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(["config.bgi", Path.Combine("sub", "logo.bmp")], _process.FilesSeen!.Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_folder, "Scripts"), "run-*"));

        // The second run takes the ZIP from the cache of the agent.
        _client.Files.Clear();
        outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(1, _client.FileDownloads);
    }

    [Fact]
    public async Task FilesThatDifferFromTheSignedHash_NeverRun()
    {
        var zip = FilesZip(("config.bgi", "BGINFO"));
        var (package, reference) = Sign("exit 0", revision: 5, filesSha256: ScriptSigningFormat.Sha256(zip));
        _client.Packages[(1, 5)] = package;
        _client.Files[(1, 5)] = FilesZip(("config.bgi", "EVIL"));

        var outcome = await Create().RunAsync(new ScriptRunRequest(Package, ScriptAction.Install, "1.0", null, null, reference), TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(ScriptRunResult.NotVerified, outcome.Result);
        Assert.Equal(0, _process.Calls);
    }
    private ScriptRunner Create(bool blocked = false) => new(_client, _process, Identity(), Trust(), State(), _time, NullLogger<ScriptRunner>.Instance, _folder)
    {
        IsBlockedLocally = () => blocked,
        PowerShellPolicyBlocker = () => null,
    };

    private static InMemoryIdentityStore Identity() => new() { Identity = new AgentIdentity(Guid.NewGuid(), "secret") };

    private MemoryTrustStateStore Trust() => new() { State = TrustState.Empty with { AnchorPublicKey = Convert.ToBase64String(_anchor.ExportSubjectPublicKeyInfo()) } };

    private ProtectedJsonFile<ScriptState>? _state;

    private ProtectedJsonFile<ScriptState> State() => _state ??= new(new ProtectedFile(Path.Combine(_folder, "scripts.dat"), "SUSyphos.Tests.Scripts", NullLogger.Instance), NullLogger.Instance);

    /// <summary>A revision signed like the central server signs it, and the reference a release list names it with.</summary>
    private (ScriptPackage Package, ScriptReference Reference) Sign(string text, int revision = 1, ScriptRunOn runOn = ScriptRunOn.Both, int timeoutSeconds = 300, IReadOnlyList<ScriptParameter>? parameters = null, IReadOnlyDictionary<string, string>? values = null, ScriptPowerShell powerShell = ScriptPowerShell.WindowsPowerShell, bool signAs51 = false, string? filesSha256 = null)
    {
        var created = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        var keySpki = _key.ExportSubjectPublicKeyInfo();
        var content = ScriptSigningFormat.NormalizeContent(text);
        var parametersJson = ScriptSigningFormat.ParametersJson(parameters ?? []);
        var contentHash = ScriptSigningFormat.Sha256(content);
        var parametersHash = ScriptSigningFormat.ParametersSha256(parametersJson);
        var signedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        var package = new ScriptPackage
        {
            ScriptId = 1,
            Revision = revision,
            PackageId = Package,
            Phase = ScriptPhase.BeforeInstall,
            RunOn = runOn,
            OnFailure = ScriptOnFailure.Stop,
            TimeoutSeconds = timeoutSeconds,
            PowerShell = powerShell,
            Content = Convert.ToBase64String(content),
            ParametersJson = parametersJson,
            SignedAt = signedAt,
            Signature = Convert.ToBase64String(_key.SignData(ScriptSigningFormat.RevisionBytes(1, revision, Package, ScriptPhase.BeforeInstall, runOn, ScriptOnFailure.Stop, timeoutSeconds, contentHash, parametersHash, signedAt, signAs51 ? ScriptPowerShell.WindowsPowerShell : powerShell, filesSha256), TrustBindingFormat.HashAlgorithm)),
            FilesSha256 = filesSha256,
            KeyId = "k20261114-test0001",
            KeyPublicKey = Convert.ToBase64String(keySpki),
            KeyCreatedAt = created,
            KeyCertificate = Convert.ToBase64String(_anchor.SignData(ScriptSigningFormat.KeyCertificateBytes("k20261114-test0001", keySpki, created), TrustBindingFormat.HashAlgorithm)),
        };
        return (package, new ScriptReference(ScriptPhase.BeforeInstall, 1, revision, contentHash, parametersHash, values));
    }

    private sealed class FakeScriptClient : IScriptClient
    {
        public Dictionary<(int, int), ScriptPackage> Packages { get; } = [];

        public List<ScriptRunReport> Runs { get; } = [];

        public Dictionary<(int, int), byte[]> Files { get; } = [];

        public int FileDownloads { get; private set; }

        public Task<byte[]?> GetFilesAsync(AgentIdentity identity, int scriptId, int revision, CancellationToken cancellationToken)
        {
            FileDownloads++;
            return Task.FromResult(Files.GetValueOrDefault((scriptId, revision)));
        }

        public Task<ScriptPackage?> GetScriptAsync(AgentIdentity identity, int scriptId, int revision, CancellationToken cancellationToken) =>
            Task.FromResult(Packages.GetValueOrDefault((scriptId, revision)));

        public Task<bool> SendRunAsync(AgentIdentity identity, ScriptRunReport report, CancellationToken cancellationToken)
        {
            Runs.Add(report);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeScriptProcess : IScriptProcess
    {
        public int? ExitCode { get; set; } = 0;

        public string Output { get; set; } = string.Empty;

        public int Calls { get; private set; }

        public string? Parameters { get; private set; }

        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public bool? AskedFor7 { get; private set; }

        public List<string>? FilesSeen { get; private set; }

        /// <summary>What the fake says it ran in when PowerShell 7 was asked for.</summary>
        public string ShellFor7 { get; set; } = PowerShellScriptProcess.PowerShell7;

        public Task<ScriptProcessResult> RunAsync(string wrapperPath, string scriptPath, string parametersJson, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken, bool powerShell7 = false)
        {
            Calls++;
            Parameters = parametersJson;
            Environment = environment;
            Assert.True(File.Exists(scriptPath));
            AskedFor7 = powerShell7;
            FilesSeen = environment.TryGetValue("SUSYPHOS_FILES", out var files) && Directory.Exists(files) ? [.. Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(files, p))] : null;
            return Task.FromResult(new ScriptProcessResult(ExitCode, Output, string.Empty, powerShell7 ? ShellFor7 : PowerShellScriptProcess.WindowsPowerShell));
        }
    }
}
