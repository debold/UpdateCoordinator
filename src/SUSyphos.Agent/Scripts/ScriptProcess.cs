using System.Diagnostics;
using System.Text;

namespace SUSyphos.Agent.Scripts;

/// <summary>What the PowerShell process of a script run returned.</summary>
/// <param name="ExitCode">Exit code, or null when it was stopped after its timeout.</param>
/// <param name="Output">The lines the wrapper wrote (time, stream, text).</param>
/// <param name="Errors">What PowerShell wrote to standard error before the wrapper ran (for example a blocked execution policy).</param>
internal sealed record ScriptProcessResult(int? ExitCode, string Output, string Errors);

/// <summary>Runs the wrapper with a script in Windows PowerShell 5.1. Separate so the runner can be tested without PowerShell.</summary>
internal interface IScriptProcess
{
    Task<ScriptProcessResult> RunAsync(string wrapperPath, string scriptPath, string parametersJson, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken);
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

    public async Task<ScriptProcessResult> RunAsync(string wrapperPath, string scriptPath, string parametersJson, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var start = new ProcessStartInfo(PowerShellPath)
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
            return new ScriptProcessResult(null, await SafeAsync(output).ConfigureAwait(false), await SafeAsync(errors).ConfigureAwait(false));
        }

        return new ScriptProcessResult(process.ExitCode, await SafeAsync(output).ConfigureAwait(false), await SafeAsync(errors).ConfigureAwait(false));
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
