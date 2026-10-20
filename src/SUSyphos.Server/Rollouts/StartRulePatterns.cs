using System.Text.RegularExpressions;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>Helpers that make start rules understandable: a pattern from an update title, and which updates a rule matches.</summary>
internal static partial class StartRulePatterns
{
    /// <summary>
    /// A title pattern that also matches the next releases of the same update: KB numbers, versions, dates and month
    /// prefixes become <c>*</c>, e.g. "Security Intelligence Update for Microsoft Defender Antivirus - KB* (Version *)".
    /// </summary>
    public static string FromTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var pattern = KbRegex().Replace(title, "KB*");
        pattern = VersionRegex().Replace(pattern, "*");
        pattern = MonthRegex().Replace(pattern, "*");
        pattern = StarsRegex().Replace(pattern, "*").Trim();
        return pattern.Length <= StartRuleEntity.MaxTitlePatternLength ? pattern : pattern[..(StartRuleEntity.MaxTitlePatternLength - 1)] + "*";
    }

    /// <summary>Waiting and started updates the rule would decide, and whether a rule with higher priority takes them first.</summary>
    public static IReadOnlyList<(UpdateEntity Update, bool TakenByOtherRule)> Matches(StartRuleEntity rule, IReadOnlyList<StartRuleEntity> otherRules, IEnumerable<UpdateEntity> updates)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var all = otherRules.Where(r => r.Id != rule.Id).Append(rule).ToList();
        return [.. updates
            .Where(u => RolloutService.FindStartRule([rule], u) is not null)
            .Select(u => (u, RolloutService.FindStartRule(all, u) != rule))];
    }

    [GeneratedRegex(@"KB\d{5,8}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KbRegex();

    [GeneratedRegex(@"\d+(\.\d+){2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MonthRegex();

    [GeneratedRegex(@"\*+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StarsRegex();
}
