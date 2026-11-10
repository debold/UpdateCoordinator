using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Inventory;

/// <summary>The installed programs and whether WinGet works here; the programs are null when they could not be read.</summary>
internal sealed record SoftwareInventoryResult(IReadOnlyList<InstalledSoftware>? Software, SoftwareSupport Support);

/// <summary>Reads the software inventory of the server. Separate so tests decide what the server has.</summary>
internal interface ISoftwareInventorySource
{
    Task<SoftwareInventoryResult> CollectAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the WinGet helper and returns its standard output. Separate so tests replace the process.</summary>
internal interface IWinGetHelperRunner
{
    /// <summary>The helper's exit code and standard output; null when it did not finish within the time.</summary>
    Task<(int ExitCode, string Output)?> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The software inventory through the WinGet helper (SUSyphos.Agent.WinGet.exe next to the agent): WinGet runs in that
/// process, so the native engine can neither crash the service nor count against its memory limit. Every entry is
/// validated against the contract again; an entry that does not pass is dropped instead of failing the inventory.
/// </summary>
internal sealed partial class WinGetHelperInventory(IWinGetHelperRunner runner, ILogger<WinGetHelperInventory> logger) : ISoftwareInventorySource
{
    /// <summary>Command of the helper that writes the inventory.</summary>
    internal const string Command = "inventory";

    /// <summary>The helper may take this long; WinGet correlates the installed programs with its sources over the network.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<SoftwareInventoryResult> CollectAsync(CancellationToken cancellationToken)
    {
        (int ExitCode, string Output)? result;
        try
        {
            result = await runner.RunAsync([Command], Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or FileNotFoundException)
        {
            LogHelperFailed(exception.Message);
            return Failed("The WinGet helper of the agent could not be started.");
        }

        if (result is not { } finished)
        {
            LogHelperTimedOut(Timeout);
            return Failed("The WinGet helper of the agent did not finish in time.");
        }

        if (finished.ExitCode != 0)
        {
            LogHelperFailed($"exit code {finished.ExitCode}");
            return Failed("The WinGet helper of the agent failed.");
        }

        SoftwareInventoryReport? report;
        try
        {
            report = JsonSerializer.Deserialize<SoftwareInventoryReport>(finished.Output, SoftwareInventoryReport.JsonOptions);
        }
        catch (JsonException exception)
        {
            LogHelperFailed(exception.Message);
            return Failed("The WinGet helper of the agent wrote no valid inventory.");
        }

        return report is null ? Failed("The WinGet helper of the agent wrote no valid inventory.") : Validated(report);
    }

    /// <summary>Keeps the entries and the support state that pass the validation of the contract.</summary>
    internal static SoftwareInventoryResult Validated(SoftwareInventoryReport report)
    {
        var software = (report.Software ?? [])
            .Where(item => item is not null && Valid(item))
            .Take(InstalledSoftware.MaxEntries)
            .ToList();
        var support = report.Support is { } reported && Enum.IsDefined(reported.State) && Valid(reported)
            ? reported
            : new SoftwareSupport { State = SoftwareSupportState.Failed, Detail = "The WinGet helper of the agent reported no valid state." };
        return new SoftwareInventoryResult(software, support);
    }

    private static bool Valid(object value) =>
        Validator.TryValidateObject(value, new ValidationContext(value), null, validateAllProperties: true);

    private static SoftwareInventoryResult Failed(string detail) =>
        new(null, new SoftwareSupport { State = SoftwareSupportState.Failed, Detail = detail });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Software inventory: the WinGet helper failed ({Reason}); the installed programs are not reported this time.")]
    private partial void LogHelperFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Software inventory: the WinGet helper did not finish within {Timeout} and was stopped.")]
    private partial void LogHelperTimedOut(TimeSpan timeout);
}

/// <summary>Starts SUSyphos.Agent.WinGet.exe from the folder of the agent, below normal priority like the agent.</summary>
/// <remarks>
/// The step lines the helper writes to standard error go into the agent log as they come, so the log shows where a
/// helper hangs before the agent stops it.
/// </remarks>
internal sealed partial class WinGetHelperRunner(ILogger<WinGetHelperRunner> logger) : IWinGetHelperRunner
{
    /// <summary>Lines longer than this are cut in the agent log (stack traces stay in the helper's own file).</summary>
    internal const int MaxLineLength = 1000;

    /// <summary>File name of the helper next to the agent.</summary>
    internal const string FileName = "SUSyphos.Agent.WinGet.exe";

    public async Task<(int ExitCode, string Output)?> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The WinGet helper is missing next to the agent.", path);
        }

        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The WinGet helper did not start.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // It may already have ended; the priority is a courtesy.
        }

        // Both streams are read at once, so a full error pipe can never block the helper.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = ForwardAsync(process.StandardError, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await errors.ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }

    private async Task ForwardAsync(StreamReader errors, CancellationToken cancellationToken)
    {
        try
        {
            while (await errors.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                {
                    LogHelperLine(line.Length > MaxLineLength ? line[..MaxLineLength] : line);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The helper ended or was stopped; its own file keeps the rest.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WinGet helper: {Line}")]
    private partial void LogHelperLine(string line);
}
