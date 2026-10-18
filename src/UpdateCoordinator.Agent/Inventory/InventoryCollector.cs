using System.Text.RegularExpressions;

using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Inventory;

/// <summary>
/// Collects the inventory and shapes it to the contract limits, so a single odd value on the
/// server never blocks the whole inventory (the central server validates again).
/// </summary>
internal sealed partial class InventoryCollector(
    ISystemProbe probe,
    IInstalledUpdateSource updates,
    RoleDetector roles,
    IAgentEnvironment environment)
{
    private const string CurrentVersionPath = AgentEnvironment.CurrentVersionPath;
    private const string WindowsUpdatePolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    public async Task<InventoryRequest> CollectAsync(CancellationToken cancellationToken)
    {
        var installed = await updates.GetInstalledUpdatesAsync(cancellationToken).ConfigureAwait(false);
        return new InventoryRequest
        {
            HostName = environment.HostName,
            DomainName = environment.DomainName,
            OperatingSystem = ReadOperatingSystem(),
            Roles = roles.Detect(),
            UpdateSource = ReadUpdateSource(),
            InstalledUpdates = installed is null ? null : Normalize(installed),
        };
    }

    private OperatingSystemInfo ReadOperatingSystem() => new()
    {
        ProductName = Text(probe.GetRegistryValue(CurrentVersionPath, "ProductName"), 128) ?? "Windows",
        EditionId = Match(probe.GetRegistryValue(CurrentVersionPath, "EditionID"), AlphanumericRegex(), 64),
        DisplayVersion = Match(probe.GetRegistryValue(CurrentVersionPath, "DisplayVersion"), VersionLabelRegex(), 16),
        Version = environment.OsVersion,
    };

    private UpdateSourceInfo ReadUpdateSource()
    {
        var useWsus = probe.GetRegistryValue(WindowsUpdatePolicyPath + @"\AU", "UseWUServer") == "1";
        var server = Text(probe.GetRegistryValue(WindowsUpdatePolicyPath, "WUServer"), 255);
        return new UpdateSourceInfo { UsesWsus = useWsus && server is not null, WsusServer = useWsus ? server : null };
    }

    internal static List<InstalledUpdate> Normalize(IReadOnlyList<RawInstalledUpdate> raw) =>
    [
        .. raw
            .Select(update => Guid.TryParse(update.UpdateId, out var id) && update.Revision >= 0
                ? new InstalledUpdate
                {
                    UpdateId = id,
                    Revision = update.Revision,
                    KbArticle = update.KbArticles.Select(kb => Match(kb.Replace("KB", string.Empty, StringComparison.OrdinalIgnoreCase), DigitsRegex(), 10)).FirstOrDefault(kb => kb is not null),
                    Title = Text(update.Title, 512) ?? id.ToString(),
                    Classification = Text(update.Classification, 64),
                }
                : null)
            .OfType<InstalledUpdate>()
            .DistinctBy(update => (update.UpdateId, update.Revision))
            .OrderBy(update => update.UpdateId)
            .ThenBy(update => update.Revision)
            .Take(InventoryRequest.MaxInstalledUpdates),
    ];

    /// <summary>Removes control characters and truncates; null for empty input.</summary>
    internal static string? Text(string? value, int maxLength)
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

    private static string? Match(string? value, Regex pattern, int maxLength) =>
        value is not null && value.Length <= maxLength && pattern.IsMatch(value) ? value : null;

    [GeneratedRegex(@"\p{Cc}+")]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex AlphanumericRegex();

    [GeneratedRegex("^[A-Za-z0-9.]+$")]
    private static partial Regex VersionLabelRegex();

    [GeneratedRegex("^[0-9]{1,10}$")]
    private static partial Regex DigitsRegex();
}
