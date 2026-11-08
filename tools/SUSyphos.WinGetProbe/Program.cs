using System.Diagnostics;
using System.Reflection;

using SUSyphos.WinGet;
using SUSyphos.WinGetProbe;

var arguments = ProbeArguments.Parse(args, out var error);
if (arguments is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ProbeArguments.Usage);
    return 2;
}

var tool = $"SUSyphos WinGet Probe {typeof(ProbeArguments).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}";
var output = new ProbeOutput(arguments.OutputFolder ?? Path.Combine(AppContext.BaseDirectory, "probe-results"), Environment.MachineName, TimeProvider.System);
var started = DateTimeOffset.UtcNow;
var watch = Stopwatch.StartNew();
output.Log($"{tool}: {string.Join(' ', args)}");

MachineInfo? machine = null;
var engineReport = new EngineReport(arguments.Engine);
object? data = null;
ProbeError? failure = null;
try
{
    machine = MachineInfo.Collect();
    output.Log($"Machine {machine.ComputerName}: {machine.ProductName} build {machine.Build}.{machine.Revision} ({machine.InstallationType}), account {machine.Account}, SYSTEM {machine.System}");
    data = await ProbeCommands.RunAsync(arguments, machine, engineReport, output);
}
catch (Exception exception)
{
    data = (exception as ProbeFailedException)?.Details;
    failure = ProbeError.From(exception);
    output.Log($"Failed: {failure.Type} {failure.HResult} {failure.Message}");
}

foreach (var diagnostics in output.CollectWinGetDiagnostics(started.UtcDateTime))
{
    output.Log($"WinGet diagnostic log: {diagnostics}");
}

var report = new ProbeReport(tool, arguments.Command, args, started, watch.Elapsed, failure is null, failure, machine, engineReport, data);
var file = output.Write(report);
output.Log($"{(failure is null ? "Done" : "Failed")} after {watch.Elapsed.TotalSeconds:0.0} s; result in {file}");
return failure is null ? 0 : 1;
