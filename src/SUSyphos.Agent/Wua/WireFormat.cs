using System.Text.RegularExpressions;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Wua;

/// <summary>
/// Shapes locally read values to the contract limits, so a single odd value never blocks a whole
/// message (the central server validates again). Shared by inventory and update report.
/// </summary>
internal static partial class WireFormat
{
    /// <summary>Removes control characters and truncates; null for empty input.</summary>
    public static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = ControlCharacterRegex().Replace(value, " ").Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }

    /// <summary>Returns the value only if it matches the pattern and length, otherwise null.</summary>
    public static string? Match(string? value, Regex pattern, int maxLength) =>
        value is not null && value.Length <= maxLength && pattern.IsMatch(value) ? value : null;

    public static List<InstalledUpdate> ToInstalledUpdates(IEnumerable<WuaUpdate> raw) =>
        Normalize(raw, InventoryRequest.MaxInstalledUpdates, (update, id) => new InstalledUpdate
        {
            UpdateId = id,
            Revision = update.Revision,
            KbArticle = Kb(update),
            Title = Text(update.Title, 512) ?? id.ToString(),
            Classification = Text(update.Classification, 64),
        });

    public static List<AvailableUpdate> ToAvailableUpdates(IEnumerable<WuaUpdate> raw) =>
        Normalize(raw, UpdateReport.MaxAvailableUpdates, (update, id) => new AvailableUpdate
        {
            UpdateId = id,
            Revision = update.Revision,
            KbArticle = Kb(update),
            Title = Text(update.Title, 512) ?? id.ToString(),
            Classification = Text(update.Classification, 64),
            MaxDownloadSizeBytes = update.MaxDownloadSize is >= 0 and <= 1_000_000_000_000 ? (long)update.MaxDownloadSize : null,
            RebootBehavior = update.RebootBehavior is { } behavior && Enum.IsDefined((RebootBehavior)behavior) ? (RebootBehavior)behavior : null,
            MsrcSeverity = Match(update.MsrcSeverity, LettersRegex(), 32),
        });

    /// <summary>History entries with a valid identity and operation, newest first, capped; odd values are cleaned, not dropped.</summary>
    public static List<UpdateHistoryEntry> ToHistory(IEnumerable<WuaHistoryRecord> raw) =>
    [
        .. raw
            .Where(r => Guid.TryParse(r.UpdateId, out _) && r.Revision >= 0 && Enum.IsDefined((UpdateOperation)r.Operation))
            .OrderByDescending(r => r.Date)
            .Take(UpdateHistoryEntry.MaxEntries)
            .Select(r =>
            {
                var id = Guid.Parse(r.UpdateId!);
                return new UpdateHistoryEntry
                {
                    UpdateId = id,
                    Revision = r.Revision,
                    Title = Text(r.Title, 512) ?? id.ToString(),
                    Date = new DateTimeOffset(DateTime.SpecifyKind(r.Date, DateTimeKind.Utc)),
                    Operation = (UpdateOperation)r.Operation,
                    ResultCode = Enum.IsDefined((InstallResultCode)r.ResultCode) ? (InstallResultCode)r.ResultCode : InstallResultCode.Failed,
                    HResult = r.HResult,
                    ClientApplicationId = Text(r.ClientApplicationId, 128),
                };
            }),
    ];

    /// <summary>Drops entries without valid identity, removes duplicates, sorts and caps the list.</summary>
    private static List<T> Normalize<T>(IEnumerable<WuaUpdate> raw, int max, Func<WuaUpdate, Guid, T> create)
        where T : UpdateInfo =>
    [
        .. raw
            .Select(update => Guid.TryParse(update.UpdateId, out var id) && update.Revision >= 0 ? create(update, id) : null)
            .OfType<T>()
            .DistinctBy(update => (update.UpdateId, update.Revision))
            .OrderBy(update => update.UpdateId)
            .ThenBy(update => update.Revision)
            .Take(max),
    ];

    private static string? Kb(WuaUpdate update) =>
        update.KbArticles
            .Select(kb => Match(kb.Replace("KB", string.Empty, StringComparison.OrdinalIgnoreCase), DigitsRegex(), 10))
            .FirstOrDefault(kb => kb is not null);

    [GeneratedRegex(@"\p{Cc}+")]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex("^[0-9]{1,10}$")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex LettersRegex();
}
