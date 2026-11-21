using System.Diagnostics;

namespace SUSyphos.Agent.Diagnostics;

/// <summary>
/// The readable Windows Update log: Windows keeps it as event trace files, Get-WindowsUpdateLog turns them into text.
/// Only in the debug mode (it takes a while and the file is large); the temporary file is deleted afterwards.
/// </summary>
internal static class WindowsUpdateLog
{
    /// <summary>Longest wait for Get-WindowsUpdateLog.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static string Produce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SUSyphos-WindowsUpdateLog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "WindowsUpdate.log");
        try
        {
            var start = new ProcessStartInfo(Scripts.PowerShellScriptProcess.PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Get-WindowsUpdateLog -LogPath $env:SUSYPHOS_WU_LOG | Out-Null" })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["SUSYPHOS_WU_LOG"] = file;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
            _ = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Get-WindowsUpdateLog did not finish within {Timeout.TotalMinutes} minutes.");
            }

            return File.Exists(file)
                ? File.ReadAllText(file)
                : throw new IOException($"Get-WindowsUpdateLog wrote no log: {errors.Result.Trim()}");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Temp is cleaned up by Windows later.
            }
        }
    }
}
