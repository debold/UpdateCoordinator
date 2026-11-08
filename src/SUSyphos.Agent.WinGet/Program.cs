using System.Text.Json;

using SUSyphos.Agent.WinGet;
using SUSyphos.Contracts.Agents;

// The agent starts the helper with one command and reads one JSON document from standard output; diagnostics go to
// standard error. Exit code 0 means the document was written, even when WinGet itself failed (it says so inside).
if (args is not [InventoryCommand.Name])
{
    await Console.Error.WriteLineAsync($"Usage: SUSyphos.Agent.WinGet {InventoryCommand.Name}");
    return 2;
}

var report = await InventoryCommand.RunAsync(Path.Combine(AppContext.BaseDirectory, InventoryCommand.EngineFolder), Console.Error);
await using var output = Console.OpenStandardOutput();
await JsonSerializer.SerializeAsync(output, report, SoftwareInventoryReport.JsonOptions);
return 0;
