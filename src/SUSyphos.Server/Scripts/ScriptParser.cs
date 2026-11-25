using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Server.Scripts;

/// <summary>A syntax error PowerShell's parser found, with its place (1-based).</summary>
internal sealed record ScriptParseError(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// The result of a syntax check: <see cref="Checked"/> false when no parser could run (then nothing is known and approval is
/// not blocked); <see cref="Parser"/> names the PowerShell that parsed.
/// </summary>
internal sealed record ScriptParseResult(
    [property: JsonPropertyName("checked")] bool Checked,
    [property: JsonPropertyName("parser")] string Parser,
    [property: JsonPropertyName("errors")] IReadOnlyList<ScriptParseError> Errors,
    [property: JsonPropertyName("problem")] string? Problem = null)
{
    [JsonPropertyName("ok")]
    public bool Ok => Errors.Count == 0;
}

/// <summary>Checks the syntax of a script. Separate so tests decide what the parser says.</summary>
internal interface IScriptParser
{
    Task<ScriptParseResult> ParseAsync(string content, ScriptPowerShell powerShell, CancellationToken cancellationToken);
}

/// <summary>
/// The syntax check of the script editor (Marc, 2026-11-22): the real parser of Windows PowerShell 5.1
/// (<c>[System.Management.Automation.Language.Parser]::ParseFile</c>), or of PowerShell 7 for a PowerShell 7 script where
/// it is installed on the central server. The script is written to a temporary file and only parsed, never run; its path
/// reaches PowerShell through an environment variable, never on the command line. Syntax only: command names and
/// parameters are not checked. Results are cached by content, at most two checks run at once, each at most 20 seconds.
/// </summary>
internal sealed partial class PowerShellScriptParser(ILogger<PowerShellScriptParser> logger) : IScriptParser, IDisposable
{
    private const string PathVariable = "SUSYPHOS_PARSE_PATH";

    // UTF-8 on standard output: otherwise PowerShell 7 writes the code page of the console, which turns the quotation
    // marks of localised messages („…“) into '"' and breaks the JSON.
    private const string Command =
        "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $tokens = $null; $errors = $null; " +
        "[void][System.Management.Automation.Language.Parser]::ParseFile($env:SUSYPHOS_PARSE_PATH, [ref]$tokens, [ref]$errors); " +
        "ConvertTo-Json -Compress -InputObject @($errors | ForEach-Object { [pscustomobject]@{ line = $_.Extent.StartLineNumber; column = $_.Extent.StartColumnNumber; message = $_.Message } })";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(2);
    private readonly ConcurrentDictionary<string, ScriptParseResult> _cache = new(StringComparer.Ordinal);

    public static string WindowsPowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string? PowerShell7Path => SUSyphos.Contracts.Scripts.PowerShell7Locator.FindLocal();

    public async Task<ScriptParseResult> ParseAsync(string content, ScriptPowerShell powerShell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var pwsh = powerShell == ScriptPowerShell.PowerShell7 ? PowerShell7Path : null;
        var use7 = pwsh is not null;
        var parser = use7 ? "PowerShell 7" : "Windows PowerShell 5.1";
        var bytes = ScriptSigningFormat.NormalizeContent(content);
        if (bytes.Length > ScriptLimits.MaxContentBytes)
        {
            return new ScriptParseResult(false, parser, [], "The script is too large to check.");
        }

        var key = $"{parser}|{ScriptSigningFormat.Sha256(bytes)}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetTempPath(), "SUSyphos", "parse");
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.ps1");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(pwsh ?? WindowsPowerShellPath, parser, path, cancellationToken).ConfigureAwait(false);
            if (result.Checked)
            {
                if (_cache.Count > 200)
                {
                    _cache.Clear();
                }

                _cache[key] = result;
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogNotChecked(exception.Message);
            return new ScriptParseResult(false, parser, [], $"The syntax could not be checked: {exception.Message}");
        }
        finally
        {
            _gate.Release();
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogNotChecked(exception.Message);
            }
        }
    }

    private static async Task<ScriptParseResult> RunAsync(string executable, string parser, string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", Command })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[PathVariable] = path;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new ScriptParseResult(false, parser, [], "The syntax check took too long.");
        }

        var json = (await output.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || json.Length == 0)
        {
            var problem = (await errors.ConfigureAwait(false)).Trim();
            return new ScriptParseResult(false, parser, [], $"The syntax check failed: {(problem.Length > 300 ? problem[..300] : problem)}");
        }

        try
        {
            var found = JsonSerializer.Deserialize<ScriptParseError[]>(json, JsonOptions) ?? [];
            return new ScriptParseResult(true, parser, [.. found.Take(50)]);
        }
        catch (JsonException exception)
        {
            return new ScriptParseResult(false, parser, [], $"The syntax check answered in an unknown form: {exception.Message}");
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script syntax check: {Reason}")]
    private partial void LogNotChecked(string reason);
}
