using System.Diagnostics;
using System.Text;

namespace SUSyphos.Agent.Scripts;

/// <summary>What the PowerShell process of a script run returned.</summary>
/// <param name="ExitCode">Exit code, or null when it was stopped after its timeout.</param>
/// <param name="Output">The lines the wrapper wrote (time, stream, text).</param>
/// <param name="Errors">What PowerShell wrote to standard error before the wrapper ran (for example a blocked execution policy).</param>
/// <param name="Shell">The PowerShell that ran it (for the log, when PowerShell 7 was asked for but missing).</param>
internal sealed record ScriptProcessResult(int? ExitCode, string Output, string Errors, string Shell = PowerShellScriptProcess.WindowsPowerShell);

/// <summary>Runs the wrapper with a script in Windows PowerShell 5.1 or PowerShell 7. Separate so the runner can be tested without PowerShell.</summary>
internal interface IScriptProcess
{
    Task<ScriptProcessResult> RunAsync(string wrapperPath, string scriptPath, string parametersJson, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken, bool powerShell7 = false);
}

/// <summary>
/// Starts <c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive
/// -ExecutionPolicy Bypass -File wrapper.ps1 -ScriptPath script.ps1</c> as the agent's account (SYSTEM) in a job object;
/// the parameters go in on standard input as JSON, never on the command line or in the environment. After the timeout the
/// job ends the whole process tree.
/// </summary>
internal sealed class PowerShellScriptProcess : IScriptProcess
{
    /// <summary>The full path, so a powershell.exe elsewhere on the PATH is never started.</summary>
    public static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// PowerShell 7 as its MSI puts it (Program Files\PowerShell\7) or, from 7.7 on, as WinGet provisions its MSIX package
    /// (Program Files\WindowsApps); null when it is not there (<see cref="SUSyphos.Contracts.Scripts.PowerShell7Locator"/>).
    /// </summary>
    public static string? PowerShell7Path => SUSyphos.Contracts.Scripts.PowerShell7Locator.FindLocal();

    /// <summary>
    /// The version of PowerShell 7 in <paramref name="path"/> ("7.5.4"), reported with the inventory (Marc, 2026-11-22: the
    /// dashboard warns about servers that run PowerShell 7 scripts without it); null when it is not installed or unreadable.
    /// </summary>
    public static string? PowerShell7Version(string? path = null) => SUSyphos.Contracts.Scripts.PowerShell7Locator.FileVersion(path ?? PowerShell7Path);

    public const string WindowsPowerShell = "Windows PowerShell 5.1";

    public const string PowerShell7 = "PowerShell 7";

    public async Task<ScriptProcessResult> RunAsync(string wrapperPath, string scriptPath, string parametersJson, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken, bool powerShell7 = false)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // PowerShell 7 only from its fixed folders under Program Files (writable only by administrators), never from the PATH;
        // without it the script runs in Windows PowerShell 5.1 (Marc, 2026-11-20).
        var pwsh = powerShell7 ? PowerShell7Path : null;
        var start = new ProcessStartInfo(pwsh ?? PowerShellPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapperPath, "-ScriptPath", scriptPath })
        {
            start.ArgumentList.Add(argument);
        }

        // Windows PowerShell 5.1 builds its own module path; one inherited from PowerShell 7 would load modules it cannot use.
        start.Environment.Remove("PSModulePath");
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var job = new JobObject();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        job.Add(process);

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(parametersJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Terminate(unchecked((uint)SUSyphos.Contracts.Scripts.ScriptLimits.TimeoutCode));
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ScriptProcessResult(null, await SafeAsync(output).ConfigureAwait(false), await SafeAsync(errors).ConfigureAwait(false), pwsh is null ? WindowsPowerShell : PowerShell7);
        }

        return new ScriptProcessResult(process.ExitCode, await SafeAsync(output).ConfigureAwait(false), await SafeAsync(errors).ConfigureAwait(false), pwsh is null ? WindowsPowerShell : PowerShell7);
    }

    private static async Task<string> SafeAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}
