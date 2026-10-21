namespace SUSyphos.Server.Administration;

/// <summary>Priorities of ordered rules (start rules, assignment rules): unique numbers from 0 to 10000, lower wins.</summary>
public static class RulePriority
{
    public const int Maximum = 10_000;

    /// <summary>Where suggestions start, so rules can still be put in front of the first one.</summary>
    public const int First = 100;

    /// <summary>
    /// Priority suggested for a new rule: the first free number after the highest one (from <see cref="First"/> on). With
    /// <paramref name="below"/> (e.g. a catch-all rule that must stay last) the suggestion stays below it where possible.
    /// </summary>
    public static int NextFree(IReadOnlyCollection<int> used, int? below = null)
    {
        ArgumentNullException.ThrowIfNull(used);
        var taken = used.ToHashSet();
        var ceiling = below ?? int.MaxValue;
        var highest = used.Where(p => p < ceiling).DefaultIfEmpty(First - 1).Max();
        for (var candidate = Math.Max(First, highest + 1); candidate < ceiling && candidate <= Maximum; candidate++)
        {
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        // No room above the highest rule: the first free value from First on (or from 0) that still stays below the ceiling.
        for (var candidate = First; candidate <= Maximum && candidate < ceiling; candidate++)
        {
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return Enumerable.Range(0, First).FirstOrDefault(p => !taken.Contains(p));
    }
}
