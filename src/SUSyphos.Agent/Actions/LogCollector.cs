using SUSyphos.Common;
using SUSyphos.Common.Diagnostics;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Actions;

/// <summary>Packs the agent's logs for an upload requested by an administrator.</summary>
internal interface ILogCollector
{
    /// <summary>A ZIP archive with the newest logs of the last days within the size limit, and the number of files in it.</summary>
    (byte[] Archive, int Files) Collect();
}

/// <summary>
/// Collects the agent's own logs first, then the further parts (the event log extract, the WinGet helper, WinGet's own
/// logs, the installers' logs in the temp folders of SYSTEM and the msiexec logs of agent updates; lab 2026-11-10 and
/// Marc 2026-11-14) through the shared <see cref="DiagnosticArchive"/>, which notes in collection.txt what it left out.
/// </summary>
internal sealed class LogCollector(string directory, TimeProvider timeProvider, IReadOnlyList<ArchivePart>? parts = null, Func<bool>? debug = null) : ILogCollector
{
    public const string FilePattern = "agent-*.log";

    /// <summary>
    /// What goes with the agent logs on a server: the event log extract, the helper's steps and each WinGet installer log,
    /// WinGet's own logs of SYSTEM, logs installers write straight into the temp folder of SYSTEM (for example dd_*.log of
    /// Burn bundles or MSI*.log), and the msiexec logs of agent updates and uninstallation.
    /// </summary>
    public static IReadOnlyList<ArchivePart> ServerParts(string agentLogDirectory, TimeProvider timeProvider)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var agentData = DataPaths.Agent;
        return
        [
            new ArchiveText("Events/Application.txt", () => EventLogExtract.Read(timeProvider.GetUtcNow().UtcDateTime.AddDays(-AgentActionList.LogDays))),
            new LogSource(Path.Combine(agentLogDirectory, "WinGet"), "*.log", "WinGet/"),
            new LogSource(Path.Combine(windows, "SystemTemp", "WinGet", "defaultState"), "WinGetCOM-*.log", "WinGetCOM/"),
            new LogSource(Path.Combine(windows, "Temp", "WinGet", "defaultState"), "WinGetCOM-*.log", "WinGetCOM/"),
            new LogSource(Path.Combine(agentData, "Update"), "*.log", "AgentUpdate/"),
            new LogSource(agentData, "uninstall.log", "AgentUpdate/"),
            new LogSource(Path.Combine(windows, "SystemTemp"), "*.log", "SystemTemp/"),
            new LogSource(Path.Combine(windows, "Temp"), "*.log", "WindowsTemp/"),
        ];
    }

    /// <summary>
    /// What the debug mode adds (Marc, 2026-11-21): the Windows Update log (made with Get-WindowsUpdateLog from the event
    /// trace files), the component based servicing log and the DISM log, which tell why a Windows update failed.
    /// </summary>
    public static IReadOnlyList<ArchivePart> DebugParts()
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs");
        return
        [
            new ArchiveText("WindowsUpdate/WindowsUpdate.log", Diagnostics.WindowsUpdateLog.Produce),
            new LogSource(Path.Combine(logs, "CBS"), "CBS.log", "CBS/"),
            new LogSource(Path.Combine(logs, "DISM"), "dism.log", "DISM/"),
        ];
    }

    public (byte[] Archive, int Files) Collect()
    {
        var result = new DiagnosticArchive(timeProvider, AgentActionList.LogDays, AgentActionList.MaxLogBytes)
            .Build($"SUSyphos agent logs, version {System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LogCollector).Assembly)?.InformationalVersion ?? "unknown"}", [new LogSource(directory, FilePattern, string.Empty), .. parts ?? [], .. debug?.Invoke() == true ? DebugParts() : []]);
        return (result.Archive, result.Files);
    }
}
