using System.Text.Json;

using SUSyphos.Agent.WinGet;
using SUSyphos.Contracts.Agents;

// The agent starts the helper with one command and reads one JSON document from standard output; diagnostics go to
// standard error. Exit code 0 means the document was written, even when WinGet itself failed (it says so inside).
var engineFolder = Path.Combine(AppContext.BaseDirectory, InventoryCommand.EngineFolder);
object? report = null;
if (args is [InventoryCommand.Name])
{
    report = await InventoryCommand.RunAsync(engineFolder, Console.Error);
}
else if (args is [InstallCommand.Name, ..])
{
    if (InstallCommand.Parse(args, out var error) is not { } install)
    {
        await Console.Error.WriteLineAsync(error);
        return 2;
    }

    var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SUSyphos", "Agent", "Logs", "WinGet");
    report = await InstallCommand.RunAsync(install.PackageId, install.Version, install.Source, install.AcceptAgreements, engineFolder, logFolder, Console.Error);
}
else
{
    await Console.Error.WriteLineAsync($"Usage: SUSyphos.Agent.WinGet {InventoryCommand.Name} | {InstallCommand.Name} <package id> <version> <source address> [{InstallCommand.AcceptAgreements}]");
    return 2;
}

await using var output = Console.OpenStandardOutput();
await JsonSerializer.SerializeAsync(output, report, report.GetType(), SoftwareInventoryReport.JsonOptions);
return 0;
