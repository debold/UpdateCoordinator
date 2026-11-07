using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SUSyphos.WinGetProbe;

/// <summary>An error as the report shows it.</summary>
public sealed record ProbeError(string Type, string HResult, string Message)
{
    public static ProbeError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = exception is AggregateException { InnerException: { } single } ? single : exception;
        return new ProbeError(inner.GetType().FullName ?? inner.GetType().Name, $"0x{inner.HResult:X8}", inner.Message);
    }
}

/// <summary>The JSON file one command writes.</summary>
public sealed record ProbeReport(
    string Tool,
    string Command,
    IReadOnlyList<string> Arguments,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    bool Succeeded,
    ProbeError? Error,
    object? Machine,
    object? Engine,
    object? Data);

/// <summary>Writes the results of the probe: one JSON file per command and one log per machine and day.</summary>
public sealed class ProbeOutput
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _logFile;
    private readonly TimeProvider _time;

    public ProbeOutput(string folder, string computerName, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);
        _time = time;
        Folder = Path.GetFullPath(folder);
        ComputerName = string.Concat(computerName.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        Directory.CreateDirectory(Folder);
        _logFile = Path.Combine(Folder, $"probe-{ComputerName}-{time.GetUtcNow():yyyyMMdd}.log");
    }

    public string Folder { get; }

    public string ComputerName { get; }

    /// <summary>Folder for the logs WinGet writes during install and upgrade.</summary>
    public string WinGetLogFolder => Path.Combine(Folder, "winget-logs");

    public void Log(string message)
    {
        var line = $"{_time.GetUtcNow():yyyy-MM-dd HH:mm:ss}Z {message}";
        Console.WriteLine(line);
        File.AppendAllText(_logFile, line + Environment.NewLine);
    }

    /// <summary>Writes the report and returns the file.</summary>
    public string Write(ProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var file = Path.Combine(Folder, $"{report.Command}-{ComputerName}-{report.StartedUtc:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(report, Json));
        return file;
    }
}
