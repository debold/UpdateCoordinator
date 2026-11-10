using System.Net;
using System.Security.Authentication;

using SUSyphos.Agent.WinGet;

namespace SUSyphos.WinGet.Tests;

/// <summary>The source check before WinGet runs and the step trace of the helper (lab, 2026-11-10).</summary>
public sealed class HelperDiagnosticsTests : IDisposable
{
    private static readonly Uri Source = new("https://central.lab.local:8443/winget/api/");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "susyphos-helper-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task TrustedSource_PassesTheCheck()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(await SourceCheck.CheckAsync(Source, () => handler));
        Assert.Equal(new Uri("https://central.lab.local:8443/winget/api/information"), handler.Requested);
    }

    [Fact]
    public async Task UntrustedCertificate_IsNamedWithItsCode()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("The SSL connection could not be established.", new AuthenticationException("The remote certificate is invalid according to the validation procedure.")));

        var problem = await SourceCheck.CheckAsync(Source, () => handler);

        Assert.Equal(SourceCheck.UntrustedCertificate, problem?.HResult);
        Assert.Contains("does not trust the certificate of the central server", problem?.Detail, StringComparison.Ordinal);
        Assert.Contains("Trust the central server's own certificate for WinGet", problem?.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SourceAnsweringWithAnError_IsReported(HttpStatusCode status)
    {
        var problem = await SourceCheck.CheckAsync(Source, () => new FakeHandler(_ => new HttpResponseMessage(status)));

        Assert.Equal(unchecked((int)0x8A15003B), problem?.HResult);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), problem?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableSource_IsReported()
    {
        var problem = await SourceCheck.CheckAsync(Source, () => new FakeHandler(_ => throw new HttpRequestException("No such host is known.")));

        Assert.NotNull(problem);
        Assert.Contains("cannot be reached", problem.Value.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_WritesTimedLines_ToStandardErrorAndTheDailyFile()
    {
        using var error = new StringWriter();
        var trace = new HelperTrace(error, _folder);

        trace.Write("Checking the WinGet source.");
        trace.Write("Progress: Downloading.");

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("Progress: Downloading.", lines[1], StringComparison.Ordinal);
        var file = Assert.Single(Directory.GetFiles(_folder, "helper-*.log"));
        Assert.Equal(lines, File.ReadAllLines(file));
    }

    [Fact]
    public void Clean_DeletesOnlyOldTraceFiles()
    {
        Directory.CreateDirectory(_folder);
        var old = Path.Combine(_folder, "helper-20260101.log");
        var recent = Path.Combine(_folder, "helper-20261110.log");
        var winget = Path.Combine(_folder, "winget-install-7zip.7zip-20260101-000000.log");
        foreach (var file in new[] { old, recent, winget })
        {
            File.WriteAllText(file, "x");
            File.SetLastWriteTime(file, file == recent ? DateTime.Now : DateTime.Now.AddDays(-30));
        }

        HelperTrace.Clean(_folder, DateTime.Now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(winget));
    }

    [Fact]
    public async Task Install_WithAnUntrustedSource_StopsBeforeWinGet()
    {
        Assert.SkipUnless(SoftwareInventory.LocalAvailability() == WinGetAvailability.Available, "WinGet is not supported on this machine.");
        using var error = new StringWriter();

        var outcome = await InstallCommand.RunAsync("7zip.7zip", "26.06", Source, false, "no-engine-needed", _folder, new HelperTrace(error, null),
            _ => Task.FromResult<(int, string)?>((SourceCheck.UntrustedCertificate, "This server does not trust the certificate of the central server.")));

        Assert.Equal((false, InstallCommand.SourceUnavailable, SourceCheck.UntrustedCertificate), (outcome.Succeeded, outcome.Status, outcome.HResult));
        Assert.DoesNotContain("Loading the WinGet engine", error.ToString(), StringComparison.Ordinal);
    }
}
