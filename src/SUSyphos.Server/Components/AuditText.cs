using System.Text;
using System.Text.RegularExpressions;

namespace SUSyphos.Server.Components;

/// <summary>
/// How the audit log reads (Marc, 2026-11-13): the area of an action as a tag and the action in words ("Logs
/// downloaded" for "Action.LogsDownloaded"), automatic actors as such, and IDs on a grey second line instead of in the
/// text. The stored entries stay as they are; this is only their display.
/// </summary>
internal static partial class AuditText
{
    private static readonly Dictionary<string, (string Label, int Index)> Areas = new(StringComparer.Ordinal)
    {
        ["Rollout"] = ("Rollout", 1),
        ["Update"] = ("Rollout", 1),
        ["Release"] = ("Rollout", 1),
        ["Action"] = ("Remote action", 2),
        ["Client"] = ("Server", 3),
        ["Server"] = ("Server", 3),
        ["CentralServer"] = ("Central server", 0),
        ["Group"] = ("Group", 3),
        ["Package"] = ("Software", 2),
        ["Software"] = ("Software", 2),
        ["User"] = ("Security", 4),
        ["Windows"] = ("Security", 4),
        ["Certificate"] = ("Security", 4),
        ["Trust"] = ("Security", 4),
    };

    /// <summary>The area of an action: the part before the dot, grouped into a few kinds; "Settings" for the rest.</summary>
    public static string Area(string action) => Areas.TryGetValue(Prefix(action), out var area) ? area.Label : "Settings";

    public static int AreaIndex(string action) => Areas.TryGetValue(Prefix(action), out var area) ? area.Index : 0;

    /// <summary>"Action.LogsDownloaded" → "Logs downloaded".</summary>
    public static string Readable(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var name = action.Contains('.', StringComparison.Ordinal) ? action[(action.IndexOf('.', StringComparison.Ordinal) + 1)..] : action;
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>Actions the central server took itself (start rules, schedules), not a person.</summary>
    public static bool IsAutomatic(string actor) => actor is "rule" or "system" or "inventory" or "schedule";

    /// <summary>
    /// Splits a GUID off a text: "LABVEX01 (371f…)" → ("LABVEX01", "371f…"), "5b0f… revision 1" → ("revision 1", "5b0f…"),
    /// a bare GUID → ("", id). Texts without one stay as they are.
    /// </summary>
    public static (string? Text, string? Id) SplitId(string? value)
    {
        if (value is null)
        {
            return (null, null);
        }

        var match = IdPattern().Match(value);
        if (!match.Success)
        {
            return (value, null);
        }

        var text = (value[..match.Index] + value[(match.Index + match.Length)..]).Replace("()", string.Empty, StringComparison.Ordinal).Trim(' ', ',', '(', ')');
        return (text, match.Groups["id"].Value);
    }

    private static string Prefix(string action) => action.Contains('.', StringComparison.Ordinal) ? action[..action.IndexOf('.', StringComparison.Ordinal)] : action;

    [GeneratedRegex(@"\(?(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IdPattern();
}
