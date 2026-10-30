using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace SUSyphos.Server.Components;

/// <summary>
/// State of a dashboard table taken from the address: search (<c>q</c>), sort (<c>sort</c>, <c>dir</c>), page
/// (<c>page</c>), grouping (<c>group</c>), view (<c>view</c>), column filters (<c>f.&lt;column&gt;</c>, repeatable) and page size (<c>size</c>). Every state is a
/// link that can be bookmarked and shared, and no script is needed. A page with several tables gives each a prefix
/// (e.g. <c>hist.</c>), so each keeps its own view and the links of one keep the view of the others. Values are only compared with known values,
/// never interpreted, so an unknown or manipulated value simply matches nothing.
/// </summary>
public sealed class TableQuery
{
    public const int DefaultPageSize = 25;
    public const int MaxSearchLength = 200;
    private const string FilterPrefix = "f.";

    /// <summary>Value of <c>view</c> that takes the filters from the address even when there are none.</summary>
    public const string CustomView = "custom";

    /// <summary>Page sizes offered below every table.</summary>
    public static readonly IReadOnlyList<int> PageSizes = [10, 25, 50, 100];

    private readonly string _path;
    private readonly Dictionary<string, StringValues> _values;
    private readonly string _prefix;

    private TableQuery(string path, Dictionary<string, StringValues> values, string defaultSort, bool defaultDescending, string prefix, int defaultPageSize, string? defaultGroup = null, IReadOnlyList<TableView>? views = null)
    {
        if (prefix.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[a-z]{1,10}\\.$"))
        {
            throw new ArgumentException("A table prefix is a few lowercase letters and a dot.", nameof(prefix));
        }

        _path = path;
        // The message of a finished action is shown once; links and forms of the list do not carry it on.
        values.Remove(DashboardPageBase.DoneParameter);
        _values = values;
        _prefix = prefix;
        var sort = Get(Key("sort"));
        Sort = string.IsNullOrEmpty(sort) ? defaultSort : sort;
        Descending = Get(Key("dir")) is { } dir ? dir == "desc" : string.IsNullOrEmpty(sort) && defaultDescending;
        Page = int.TryParse(Get(Key("page")), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1;
        PageSize = int.TryParse(Get(Key("size")), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && PageSizes.Contains(size) ? size : defaultPageSize;
        var search = Get(Key("q"))?.Trim();
        Search = string.IsNullOrEmpty(search) ? null : search.Length > MaxSearchLength ? search[..MaxSearchLength] : search;
        // A list grouped by default stays ungrouped once "No grouping" was chosen (an empty value in the address).
        Group = values.ContainsKey(Key("group")) ? Get(Key("group")) is { Length: > 0 } group ? group : null : defaultGroup;
        Views = views ?? [];
        var view = Get(Key("view"));
        var custom = view == CustomView || values.Keys.Any(k => k.StartsWith(_prefix + FilterPrefix, StringComparison.Ordinal));
        RequestedView = custom || Views.Count == 0 ? null : Views.FirstOrDefault(v => v.Key == view) ?? Views[0];
        EffectiveFilters = RequestedView?.Filters ?? values.Keys
            .Where(k => k.StartsWith(_prefix + FilterPrefix, StringComparison.Ordinal))
            .Select(k => (Column: k[(_prefix.Length + FilterPrefix.Length)..], Filter: TableFilter.FromAddress(values[k])))
            .Where(f => f.Column.Length > 0 && f.Filter is not null)
            .ToDictionary(f => f.Column, f => f.Filter!, StringComparer.Ordinal);
    }

    /// <summary>The views of the table, default first; empty for a table without views.</summary>
    public IReadOnlyList<TableView> Views { get; }

    /// <summary>
    /// The view the address asks for (<c>view=&lt;key&gt;</c>, else the default view); null when the address sets the
    /// filters itself (any <c>f.*</c> value or <c>view=custom</c>) or the table has no views.
    /// </summary>
    public TableView? RequestedView { get; }

    /// <summary>The filters in effect by column: those of the requested view, else those in the address.</summary>
    public IReadOnlyDictionary<string, TableFilter> EffectiveFilters { get; }

    public string ViewField => Key("view");

    /// <summary>Rows per page: one of <see cref="PageSizes"/>.</summary>
    public int PageSize { get; }

    /// <summary>Name of the search field in the address.</summary>
    public string SearchField => Key("q");

    public string GroupField => Key("group");

    public string SizeField => Key("size");

    /// <summary>Prefix of this table in ids of the page, e.g. "hist-" (empty for the only table of a page).</summary>
    public string IdPrefix => _prefix.Replace(".", "-", StringComparison.Ordinal);

    public string? Search { get; }

    public string Sort { get; }

    public bool Descending { get; }

    public int Page { get; }

    public string? Group { get; }

    /// <summary>Key of the row whose details are open (expanded row); null when none is.</summary>
    public string? Open => Get(Key("open")) is { Length: > 0 } open ? open : null;

    /// <summary>Opens the details of a row, or closes them when they are open; the rest of the view stays.</summary>
    public string ToggleLink(string rowKey) => With(values =>
    {
        if (Open == rowKey)
        {
            values.Remove(Key("open"));
        }
        else
        {
            values[Key("open")] = rowKey;
        }
    });

    /// <param name="navigation">Current address.</param>
    /// <param name="defaultSort">Column sorted by when the address names none.</param>
    /// <param name="defaultDescending">True to list the default column newest or largest first.</param>
    /// <param name="prefix">Prefix of the address values when a page has several tables, e.g. "hist."; empty for one table.</param>
    /// <param name="defaultPageSize">Rows per page when the address names none.</param>
    /// <param name="defaultGroup">Column the list is grouped by when the address names no grouping.</param>
    /// <param name="views">The views of the table, default first (see <see cref="TableView"/>).</param>
    public static TableQuery From(NavigationManager navigation, string defaultSort, bool defaultDescending = false, string prefix = "", int defaultPageSize = DefaultPageSize, string? defaultGroup = null, IReadOnlyList<TableView>? views = null)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var uri = new Uri(navigation.Uri);
        return new TableQuery(uri.AbsolutePath, QueryHelpers.ParseQuery(uri.Query), defaultSort, defaultDescending, prefix, defaultPageSize, defaultGroup, views);
    }

    /// <summary>For tests and pages that build a query from known values.</summary>
    public static TableQuery Parse(string pathAndQuery, string defaultSort, string prefix = "", IReadOnlyList<TableView>? views = null)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var index = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return index < 0
            ? new TableQuery(pathAndQuery, [], defaultSort, false, prefix, DefaultPageSize, views: views)
            : new TableQuery(pathAndQuery[..index], QueryHelpers.ParseQuery(pathAndQuery[index..]), defaultSort, false, prefix, DefaultPageSize, views: views);
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value.ToString() : null;

    /// <summary>The filter of a column in effect (from the view or the address); null means no filter.</summary>
    public TableFilter? Filter(string column) => EffectiveFilters.GetValueOrDefault(column);

    /// <summary>Name of a column filter in the address.</summary>
    public string FilterField(string column) => _prefix + FilterPrefix + column;

    public string SortLink(string column) =>
        With(values =>
        {
            var descending = Sort == column && !Descending;
            values[Key("sort")] = column;
            values[Key("dir")] = descending ? "desc" : "asc";
            values.Remove(Key("page"));
        });

    public string PageLink(int page) => With(values => values[Key("page")] = page.ToString(CultureInfo.InvariantCulture));

    /// <summary>The filters in effect with the filter of one column replaced (null removes it); this leaves the view.</summary>
    public string WithFilter(string column, TableFilter? filter) =>
        With(values =>
        {
            var filters = new Dictionary<string, TableFilter>(EffectiveFilters, StringComparer.Ordinal);
            if (filter is null)
            {
                filters.Remove(column);
            }
            else
            {
                filters[column] = filter;
            }

            WriteFilters(values, filters);
        });

    /// <summary>Shows a view: its filters replace those in the address; the default view needs no value.</summary>
    public string ViewLink(TableView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return With(values =>
        {
            RemoveFilters(values);
            if (Views.Count == 0 || view != Views[0])
            {
                values[Key("view")] = view.Key;
            }
        });
    }

    /// <summary>Groups by a column, or ungroups with null (an empty value, so a list grouped by default stays ungrouped).</summary>
    public string GroupLink(string? column) =>
        With(values =>
        {
            values[Key("group")] = column ?? string.Empty;
            values.Remove(Key("page"));
        });

    /// <summary>
    /// Hidden fields of the filter form of a column: the rest of the address and the other filters in effect (also
    /// those of a view), so the checked values of the column replace only its own filter.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> FilterFormFields(string column)
    {
        var values = new Dictionary<string, StringValues>(_values, StringComparer.Ordinal);
        WriteFilters(values, EffectiveFilters.Where(f => f.Key != column).ToDictionary(StringComparer.Ordinal), form: true);
        return values.SelectMany(p => p.Value.Where(v => v is not null).Select(v => new KeyValuePair<string, string>(p.Key, v!)));
    }

    public string With(string key, string? value) =>
        With(values =>
        {
            if (string.IsNullOrEmpty(value))
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        });

    /// <summary>
    /// Hidden fields that keep the rest of the view (and the views of other tables on the page) when a GET form changes
    /// some of it; <paramref name="keys"/> are full names in the address, e.g. <see cref="SearchField"/>. The page of
    /// this table starts over.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> HiddenFieldsExcept(params string[] keys) =>
        _values.Where(p => !keys.Contains(p.Key) && p.Key != Key("page"))
            .SelectMany(p => p.Value.Where(v => v is not null).Select(v => new KeyValuePair<string, string>(p.Key, v!)));

    private string Key(string name) => _prefix + name;

    private void RemoveFilters(Dictionary<string, StringValues> values)
    {
        foreach (var key in values.Keys.Where(k => k.StartsWith(_prefix + FilterPrefix, StringComparison.Ordinal)).ToList())
        {
            values.Remove(key);
        }

        values.Remove(Key("view"));
        values.Remove(Key("page"));
    }

    /// <summary>Writes filters into the address and drops the view; <c>view=custom</c> keeps "no filter" apart from "default view".</summary>
    private void WriteFilters(Dictionary<string, StringValues> values, Dictionary<string, TableFilter> filters, bool form = false)
    {
        RemoveFilters(values);
        foreach (var (column, filter) in filters)
        {
            values[FilterField(column)] = filter.ToAddress().ToArray();
        }

        // A form always keeps it: unchecking every value of the only filter must not bring back the default view. Without
        // filters it is needed only when the default view filters.
        if (Views.Count > 0 && (form || (filters.Count == 0 && Views[0].Filters.Count > 0)))
        {
            values[Key("view")] = CustomView;
        }
    }

    private string With(Action<Dictionary<string, StringValues>> change)
    {
        var values = new Dictionary<string, StringValues>(_values, StringComparer.Ordinal);
        change(values);
        var query = string.Join("&", values.SelectMany(p => p.Value.Where(v => v is not null).Select(v => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(v!)}")));
        return query.Length == 0 ? _path : $"{_path}?{query}";
    }
}

/// <summary>A column a table can be sorted, filtered or grouped by.</summary>
/// <param name="Key">Name in the address.</param>
/// <param name="Label">Header text.</param>
/// <param name="SortKey">Value used for sorting.</param>
/// <param name="FilterValues">Values used for filtering and grouping (several for e.g. group memberships); null if the column has no filter.</param>
/// <param name="Class">CSS class of the header cell, e.g. "n" for numbers.</param>
/// <param name="Sortable">False for a column that is only shown (e.g. details); its sort key is never used.</param>
public sealed record TableColumn<T>(string Key, string Label, Func<T, IComparable?> SortKey, Func<T, IReadOnlyList<string>>? FilterValues = null, string? Class = null, bool Sortable = true)
{
    /// <summary>Group a row belongs to when the list is grouped by this column: its first filter value.</summary>
    public string GroupKey(T row) => FilterValues?.Invoke(row) is [var first, ..] ? first : string.Empty;
}

/// <summary>One page of rows after search, filters and sorting, with the filter options and their counts and the current view (null for a table without views).</summary>
public sealed record TablePage<T>(IReadOnlyList<T> Rows, int Matching, int Total, int Page, int Pages, IReadOnlyDictionary<string, IReadOnlyList<FilterOption>> Options, int PageSize = TableQuery.DefaultPageSize, TableViewState? View = null);

public sealed record FilterOption(string Value, int Count);

public static class TablePaging
{
    /// <summary>Applies a table query to rows in memory (the dashboard handles thousands of servers, not millions).</summary>
    public static TablePage<T> Apply<T>(IReadOnlyList<T> rows, TableQuery query, IReadOnlyList<TableColumn<T>> columns, Func<T, string> searchText)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);
        var pageSize = query.PageSize;
        IEnumerable<T> filtered = rows;
        if (query.Search is { } search)
        {
            filtered = filtered.Where(r => searchText(r).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var column in columns.Where(c => c.FilterValues is not null))
        {
            if (query.Filter(column.Key) is { } filter)
            {
                filtered = filtered.Where(r => filter.Matches(column.FilterValues!(r)));
            }
        }

        var sort = columns.FirstOrDefault(c => c.Key == query.Sort && c.Sortable) ?? columns[0];

        // Grouped lists are ordered by group first, so a group is not split across pages.
        var group = columns.FirstOrDefault(c => c.Key == query.Group && c.FilterValues is not null);
        var grouped = group is null
            ? filtered.OrderBy(_ => 0)
            : filtered.OrderBy(group.GroupKey, StringComparer.CurrentCulture);
        var ordered = query.Descending
            ? grouped.ThenByDescending(sort.SortKey, NullSafeComparer.Instance)
            : grouped.ThenBy(sort.SortKey, NullSafeComparer.Instance);
        var matching = ordered.ToList();
        var pages = Math.Max(1, (matching.Count + pageSize - 1) / pageSize);
        var page = Math.Min(query.Page, pages);

        var options = columns.Where(c => c.FilterValues is not null).ToDictionary(
            c => c.Key,
            c => (IReadOnlyList<FilterOption>)[.. rows.SelectMany(r => c.FilterValues!(r).Distinct(StringComparer.Ordinal)).GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.CurrentCulture).Select(g => new FilterOption(g.Key, g.Count()))]);

        var known = options.ToDictionary(o => o.Key, o => (IReadOnlyList<string>)[.. o.Value.Select(v => v.Value)], StringComparer.Ordinal);
        var view = TableViewState.Of(query, known, v => rows.Count(r => v.Matches(r, columns)));
        return new TablePage<T>([.. matching.Skip((page - 1) * pageSize).Take(pageSize)], matching.Count, rows.Count, page, pages, options, pageSize, view);
    }

    /// <summary>
    /// Rows of the page in their groups when the address asks for grouping by a filterable column (one group with a null
    /// name otherwise). Apply already ordered the rows by group, so every list renders its groups the same way.
    /// </summary>
    public static IEnumerable<(string? Group, IReadOnlyList<T> Rows)> Groups<T>(TablePage<T> page, TableQuery query, IReadOnlyList<TableColumn<T>> columns)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);
        var column = columns.FirstOrDefault(c => c.Key == query.Group && c.FilterValues is not null);
        if (column is null)
        {
            return [(null, page.Rows)];
        }

        return page.Rows.GroupBy(column.GroupKey).Select(g => ((string?)g.Key, (IReadOnlyList<T>)[.. g]));
    }

    /// <summary>Labels of the filterable columns, for the filter chips.</summary>
    public static IReadOnlyDictionary<string, string> Labels<T>(IReadOnlyList<TableColumn<T>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return columns.Where(c => c.FilterValues is not null).ToDictionary(c => c.Key, c => c.Label, StringComparer.Ordinal);
    }

    private sealed class NullSafeComparer : IComparer<IComparable?>
    {
        public static readonly NullSafeComparer Instance = new();

        public int Compare(IComparable? x, IComparable? y) =>
            x is null ? (y is null ? 0 : 1)
            : y is null ? -1
            : x is string a && y is string b ? StringComparer.CurrentCultureIgnoreCase.Compare(a, b)
            : x.CompareTo(y);
    }
}
