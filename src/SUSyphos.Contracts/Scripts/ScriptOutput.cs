using System.Globalization;
using System.Text;

namespace SUSyphos.Contracts.Scripts;

/// <summary>One line of a script output: when, which stream and the text.</summary>
public sealed record ScriptOutputLine(DateTimeOffset? At, string Stream, string Text);

/// <summary>
/// The output of a script run (Marc, 2026-11-13: every stream logged, traceable on the central server), shared by agent
/// and central server: each record is one line "<c>yyyy-MM-ddTHH:mm:ss.fffZ [Stream] text</c>"; secret values are
/// masked; beyond <see cref="ScriptLimits.MaxOutputBytes"/> the head and the tail are kept with a note between.
/// </summary>
public static class ScriptOutput
{
    /// <summary>The streams the wrapper writes, in the order the filter offers them.</summary>
    public static readonly IReadOnlyList<string> Streams = ["Output", "Error", "Warning", "Verbose", "Debug", "Information", "Host", "Runner"];

    public static string Line(DateTimeOffset at, string stream, string text) =>
        string.Create(CultureInfo.InvariantCulture, $"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} [{stream}] {text}");

    /// <summary>Replaces every secret value (non-empty) with the mask.</summary>
    public static string Mask(string text, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var secret in (secrets ?? []).Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, ScriptLimits.Mask, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>The output within the limit: unchanged, or head and tail with a note; true when it was cut.</summary>
    public static (string? Output, bool Truncated) Limit(string? output, int maxBytes = ScriptLimits.MaxOutputBytes)
    {
        if (output is null || Encoding.UTF8.GetByteCount(output) <= maxBytes)
        {
            return (output, false);
        }

        // Characters are at most 3 bytes in UTF-8 for this text (no unpaired surrogates survive the agent's writer).
        var half = maxBytes / 2 / 3;
        var head = output[..half];
        var tail = output[^half..];
        var cut = output.Length - head.Length - tail.Length;
        var note = string.Create(CultureInfo.InvariantCulture, $"{Environment.NewLine}… truncated: {cut:N0} characters in the middle were left out to stay within {maxBytes / 1024} KB …{Environment.NewLine}");
        return (head[..(head.LastIndexOf('\n') + 1 is > 0 and var h ? h : head.Length)] + note + tail[(tail.IndexOf('\n') + 1)..], true);
    }

    /// <summary>Lines, error lines and warning lines of an output.</summary>
    public static (int Lines, int Errors, int Warnings) Count(string? output)
    {
        var lines = Parse(output).ToList();
        return (lines.Count, lines.Count(l => l.Stream == "Error"), lines.Count(l => l.Stream == "Warning"));
    }

    /// <summary>The lines of an output; a line without the prefix continues as its own line of the stream "Output".</summary>
    public static IEnumerable<ScriptOutputLine> Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            yield break;
        }

        foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
        {
            // PowerShell may start its UTF-8 output with a byte order mark.
            var raw = line.TrimStart('\uFEFF');
            if (raw.Length == 0)
            {
                continue;
            }

            var close = raw.IndexOf("] ", StringComparison.Ordinal);
            if (raw.Length > 26 && raw[24] == ' ' && raw[25] == '[' && close > 26
                && DateTimeOffset.TryParseExact(raw[..24], "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                yield return new ScriptOutputLine(at, raw[26..close], raw[(close + 2)..]);
            }
            else
            {
                yield return new ScriptOutputLine(null, "Output", raw);
            }
        }
    }
}
