namespace SUSyphos.Server.Components;

/// <summary>
/// The filter of one column: the values shown, or the values hidden (<see cref="Exclude"/>). In the address an
/// excluded value carries a leading "!" (<c>f.rollout=!Superseded</c>), so "everything except" stays correct when new
/// values appear. Values are only compared, never interpreted.
/// </summary>
public sealed record TableFilter(bool Exclude, IReadOnlyList<string> Values)
{
    public const char ExcludeMark = '!';

    public static TableFilter In(params string[] values) => new(false, values);

    public static TableFilter Not(params string[] values) => new(true, values);

    /// <summary>True when a row with these values is shown: one of them is selected (and, for an exclusion, not hidden).</summary>
    public bool Matches(IEnumerable<string> rowValues)
    {
        ArgumentNullException.ThrowIfNull(rowValues);
        return Exclude
            ? rowValues.DefaultIfEmpty(string.Empty).Any(v => !Values.Contains(v, StringComparer.Ordinal))
            : rowValues.Any(v => Values.Contains(v, StringComparer.Ordinal));
    }

    /// <summary>The values as written in the address.</summary>
    public IEnumerable<string> ToAddress() => Exclude ? Values.Select(v => ExcludeMark + v) : Values;

    /// <summary>Reads the values of one column from the address; null when there are none.</summary>
    public static TableFilter? FromAddress(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Where(v => v is not null).Select(v => v!).ToList();
        var excluded = list.Where(v => v.Length > 0 && v[0] == ExcludeMark).Select(v => v[1..]).Distinct(StringComparer.Ordinal).ToArray();
        if (excluded.Length > 0)
        {
            return Not(excluded);
        }

        // An empty value stands for rows without a value ("(none)" in the filter).
        var included = list.Distinct(StringComparer.Ordinal).ToArray();
        return included.Length > 0 ? In(included) : null;
    }

    /// <summary>
    /// The shortest form of a selection among the known values: null when everything is selected, an exclusion when
    /// more than half is selected, else the selected values. Forms send the checked values; chips and view names use
    /// this form, so "everything but Superseded" reads the same however it was chosen.
    /// </summary>
    public static TableFilter? Of(IReadOnlyCollection<string> selected, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(known);
        if (known.Count > 0 && known.All(selected.Contains))
        {
            return null;
        }

        return selected.Count * 2 > known.Count && known.Count > 0
            ? Not([.. known.Where(k => !selected.Contains(k))])
            : In([.. selected]);
    }
}

/// <summary>
/// A view of a table: a fixed set of column filters with a name, defined in code (no saved or own views; Marc,
/// 2026-10-30). The first view of a table is its default, shown when the address names neither a view nor filters.
/// Search, sort, grouping and page size stay independent of the view.
/// </summary>
public sealed record TableView(string Key, string Name, string Description, IReadOnlyDictionary<string, TableFilter> Filters)
{
    public TableView(string key, string name, string description, params (string Column, TableFilter Filter)[] filters)
        : this(key, name, description, filters.ToDictionary(f => f.Column, f => f.Filter, StringComparer.Ordinal))
    {
    }

    /// <summary>True when a row passes every filter of the view.</summary>
    public bool Matches<T>(T row, IReadOnlyList<TableColumn<T>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return Filters.All(f => columns.FirstOrDefault(c => c.Key == f.Key)?.FilterValues is not { } values || f.Value.Matches(values(row)));
    }
}

/// <summary>
/// Which view a table shows and how many rows each view has. The name follows the filters: when they match a view
/// exactly, that view is current, however the filters were set; otherwise the table shows "Custom".
/// </summary>
/// <param name="Views">The views of the table, default first.</param>
/// <param name="Current">The view the filters match; null for custom filters.</param>
/// <param name="Counts">Rows of each view by key, without search.</param>
/// <param name="Filters">The active filters by column: as the current view writes them, else in their shortest form (see <see cref="TableFilter.Of"/>).</param>
public sealed record TableViewState(IReadOnlyList<TableView> Views, TableView? Current, IReadOnlyDictionary<string, int> Counts, IReadOnlyDictionary<string, TableFilter> Filters)
{
    public TableView Default => Views[0];

    public bool IsDefault => Current == Default;

    /// <summary>Works out the current view from the address and the known values of each filter column.</summary>
    /// <param name="query">The address of the table.</param>
    /// <param name="known">Known values of each filterable column (the filter options).</param>
    /// <param name="count">Rows of a view.</param>
    public static TableViewState? Of(TableQuery query, IReadOnlyDictionary<string, IReadOnlyList<string>> known, Func<TableView, int> count)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(count);
        var normalized = Normalize(query.EffectiveFilters, known);
        if (query.Views.Count == 0)
        {
            return null;
        }

        var current = query.RequestedView
            ?? query.Views.FirstOrDefault(v => Same(Normalize(v.Filters, known), normalized));
        // The chips of a view read as the view is written ("not Superseded, …"), custom filters in their shortest form.
        return new TableViewState(query.Views, current, query.Views.ToDictionary(v => v.Key, count, StringComparer.Ordinal), current?.Filters ?? normalized);
    }

    /// <summary>The filters in their shortest form among the known values of each column (see <see cref="TableFilter.Of"/>).</summary>
    public static Dictionary<string, TableFilter> Normalize(IReadOnlyDictionary<string, TableFilter> filters, IReadOnlyDictionary<string, IReadOnlyList<string>> known)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(known);
        var result = new Dictionary<string, TableFilter>(StringComparer.Ordinal);
        foreach (var (column, filter) in filters)
        {
            if (known.TryGetValue(column, out var values) && values.Count > 0)
            {
                var selected = values.Where(v => filter.Matches([v])).ToHashSet(StringComparer.Ordinal);
                if (TableFilter.Of(selected, values) is { } shortest)
                {
                    result[column] = shortest;
                }
            }
            else
            {
                // Without known values (nothing to compare with) the filter is kept as written.
                result[column] = filter;
            }
        }

        return result;
    }

    private static bool Same(Dictionary<string, TableFilter> a, Dictionary<string, TableFilter> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && other.Exclude == p.Value.Exclude && other.Values.ToHashSet(StringComparer.Ordinal).SetEquals(p.Value.Values));
}
