using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using SUSyphos.WinGetProbe;

namespace SUSyphos.WinGet.Tests;

public sealed class ProbeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("susyphos-probe-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static ProbeArguments Parse(params string[] args) =>
        ProbeArguments.Parse(args, out var error) ?? throw new Xunit.Sdk.XunitException(error);

    private static string Reject(params string[] args)
    {
        Assert.Null(ProbeArguments.Parse(args, out var error));
        return error!;
    }

    [Fact]
    public void Parse_ReadsAnInstall()
    {
        var arguments = Parse("install", "Microsoft.VCRedist.2015+.x64", "--version", "14.38.33135.0", "--source", "https://central.example:8443/winget/", "--engine", "Bundled", "--accept-agreements", "--out", @"C:\results");

        Assert.Equal(new ProbeArguments("install", "Microsoft.VCRedist.2015+.x64", "14.38.33135.0", new Uri("https://central.example:8443/winget/"), EngineChoice.Bundled, true, false, null, @"C:\results"), arguments);
    }

    [Fact]
    public void Parse_ReadsServe()
    {
        var arguments = Parse("serve", "--cache", @"D:\cache", "--port", "8443", "--certificate", "0123456789abcdef0123456789abcdef01234567");

        Assert.Equal((@"D:\cache", 8443, "0123456789ABCDEF0123456789ABCDEF01234567"), (arguments.CacheFolder, arguments.Port, arguments.Certificate));
    }

    [Fact]
    public void Parse_HasSafeDefaults()
    {
        var arguments = Parse("upgrade", "7zip.7zip");

        Assert.Equal(EngineChoice.Installed, arguments.Engine);
        Assert.False(arguments.AcceptAgreements);
        Assert.Null(arguments.Version);
        Assert.Null(arguments.Source);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("inventory", "--engine", "installed")]
    [InlineData("bootstrap", "--online")]
    [InlineData("bootstrap", "--offline", @"C:\winget offline")]
    [InlineData("search", "notepad++")]
    [InlineData("search", "Notepad", "--source", "https://central/winget")]
    [InlineData("uninstall", "Notepad++.Notepad++", "--source", "https://central/winget")]
    [InlineData("cache", "7zip.7zip", "--version", "24.08", "--cache", @"C:\winget-cache")]
    [InlineData("serve", "--port", "8443", "--certificate", "0123456789abcdef0123456789abcdef01234567")]
    public void Parse_AcceptsEveryCommand(params string[] args)
    {
        Assert.Equal(args[0], Parse(args).Command);
    }

    [Theory]
    [InlineData("No command.")]
    [InlineData("Unknown command 'remove'.", "remove", "7zip.7zip")]
    [InlineData("Unknown command 'INFO'.", "INFO")]
    [InlineData("install needs a package id.", "install")]
    [InlineData("search needs a search text.", "search")]
    [InlineData("info takes no 'extra'.", "info", "extra")]
    [InlineData("Unexpected argument 'b'.", "install", "a", "b")]
    [InlineData("Unexpected argument '--force'.", "install", "a", "--force")]
    [InlineData("'7zip.7zip;calc' is not a WinGet package id.", "install", "7zip.7zip;calc")]
    [InlineData("'.hidden' is not a WinGet package id.", "install", ".hidden")]
    [InlineData("'..\\..\\x' is not a WinGet package id.", "upgrade", "..\\..\\x")]
    [InlineData("'1.0 && calc' is not a version.", "install", "a", "--version", "1.0 && calc")]
    [InlineData("--version needs a value.", "install", "a", "--version")]
    [InlineData("--version needs a value.", "install", "a", "--version", "--accept-agreements")]
    [InlineData("'http://central/winget' is not an https address.", "install", "a", "--source", "http://central/winget")]
    [InlineData("'https://user:pw@central/' is not an https address.", "install", "a", "--source", "https://user:pw@central/")]
    [InlineData("'central/winget' is not an https address.", "install", "a", "--source", "central/winget")]
    [InlineData("Unknown engine 'system' (installed or bundled).", "info", "--engine", "system")]
    [InlineData("Unknown engine '1' (installed or bundled).", "info", "--engine", "1")]
    [InlineData("bootstrap needs either --online or --offline <folder>.", "bootstrap")]
    [InlineData("bootstrap needs either --online or --offline <folder>.", "bootstrap", "--online", "--offline", "C:\\w")]
    [InlineData("--online and --offline belong to bootstrap.", "info", "--online")]
    [InlineData("--version belongs to install, upgrade and cache; --accept-agreements to install and upgrade.", "search", "x", "--accept-agreements")]
    [InlineData("--version belongs to install, upgrade and cache; --accept-agreements to install and upgrade.", "info", "--version", "1.0")]
    [InlineData("--version belongs to install, upgrade and cache; --accept-agreements to install and upgrade.", "cache", "a.b", "--accept-agreements")]
    [InlineData("--version belongs to install, upgrade and cache; --accept-agreements to install and upgrade.", "uninstall", "a.b", "--version", "1.0")]
    [InlineData("uninstall needs a package id.", "uninstall")]
    [InlineData("cache needs a package id.", "cache")]
    [InlineData("'a b' is not a WinGet package id.", "cache", "a b")]
    [InlineData("serve takes no 'x'.", "serve", "x", "--certificate", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("serve needs --certificate <thumbprint>.", "serve")]
    [InlineData("'0' is not a port.", "serve", "--port", "0")]
    [InlineData("'65536' is not a port.", "serve", "--port", "65536")]
    [InlineData("'+80' is not a port.", "serve", "--port", "+80")]
    [InlineData("'abc' is not a certificate thumbprint.", "serve", "--certificate", "abc")]
    [InlineData("--cache belongs to cache and serve.", "info", "--cache", "cachefolder")]
    [InlineData("--port and --certificate belong to serve.", "cache", "a.b", "--port", "80")]
    [InlineData("--source belongs to search, install, upgrade and uninstall.", "inventory", "--source", "https://central/")]
    public void Parse_RejectsBadInput(string expected, params string[] args)
    {
        Assert.Equal(expected, Reject(args));
    }

    [Fact]
    public void Parse_RejectsControlCharactersAndLongSearches()
    {
        Assert.Contains("control characters", Reject("search", "a\u0000b"), StringComparison.Ordinal);
        Assert.Contains("too long", Reject("search", new string('a', 201)), StringComparison.Ordinal);
        Assert.Contains("not a WinGet package id", Reject("install", new string('a', 129)), StringComparison.Ordinal);
    }

    [Fact]
    public void Output_WritesTheReportAndTheLog()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.Zero));
        var output = new ProbeOutput(Path.Combine(_folder, "results"), "SRV 2019/01", time);

        output.Log("first");
        var file = output.Write(new ProbeReport("Probe 0.1", "info", ["info"], time.GetUtcNow(), TimeSpan.FromSeconds(2), false, ProbeError.From(new InvalidOperationException("broken")), null, null, new { Count = 3 }));

        Assert.Equal(Path.Combine(_folder, "results", "info-SRV_2019_01-20261107-093000.json"), file);
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        Assert.False(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("System.InvalidOperationException", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        Assert.Equal("2026-11-07 09:30:00Z first" + Environment.NewLine, File.ReadAllText(Path.Combine(_folder, "results", "probe-SRV_2019_01-20261107.log")));
        Assert.Equal(Path.Combine(_folder, "results", "winget-logs"), output.WinGetLogFolder);
    }

    [Fact]
    public void Output_CollectsTheDiagnosticLogsOfThisRunOnly()
    {
        var temp = Path.Combine(_folder, "temp");
        var state = Directory.CreateDirectory(Path.Combine(temp, "WinGet", "defaultState")).FullName;
        var old = Path.Combine(state, "WinGetCOM-2026-11-01-10-00-00.000.log");
        var current = Path.Combine(state, "WinGetCOM-2026-11-08-09-00-00.000.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(current, "current");
        File.WriteAllText(Path.Combine(state, "other.log"), "other");
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(current, new DateTime(2026, 11, 8, 9, 0, 5, DateTimeKind.Utc));
        var output = new ProbeOutput(Path.Combine(_folder, "results"), "SRV", TimeProvider.System);

        var copied = output.CollectWinGetDiagnostics(new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc), temp);

        Assert.Equal([Path.Combine(output.WinGetLogFolder, Path.GetFileName(current))], copied);
        Assert.Equal("current", File.ReadAllText(copied[0]));
    }

    [Fact]
    public void Output_CollectsNothingWithoutWinGetState()
    {
        var output = new ProbeOutput(Path.Combine(_folder, "results"), "SRV", TimeProvider.System);

        Assert.Empty(output.CollectWinGetDiagnostics(DateTime.MinValue, Path.Combine(_folder, "missing")));
        Assert.False(Directory.Exists(output.WinGetLogFolder));
    }

    [Fact]
    public void Error_UnwrapsASingleInnerException()
    {
        var error = ProbeError.From(new AggregateException(new UnauthorizedAccessException("denied")));

        Assert.Equal(new ProbeError("System.UnauthorizedAccessException", "0x80070005", "denied"), error);
    }
}
