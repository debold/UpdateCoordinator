using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace SUSyphos.Server.Components;

/// <summary>
/// State of a dashboard table taken from the address: search (<c>q</c>), sort (<c>sort</c>, <c>dir</c>), page
/// (<c>page</c>), grouping (<c>group</c>) and column filters (<c>f.&lt;column&gt;</c>, repeatable) and page size (<c>size</c>). Every view is a
/// link that can be bookmarked and shared, and no script is needed. A page with several tables gives each a prefix
/// (e.g. <c>hist.</c>), so each keeps its own view and the links of one keep the view of the others. Values are only compared with known values,
/// never interpreted, so an unknown or manipulated value simply matches nothing.
/// </summary>
public sealed class TableQuery
{
    public const int DefaultPageSize = 25;
    public const int MaxSearchLength = 200;
    private const string FilterPrefix = "f.";

    /// <summary>Page sizes offered below every table.</summary>
    public static readonly IReadOnlyList<int> PageSizes = [10, 25, 50, 100];

    private readonly string _path;
    private readonly Dictionary<string, StringValues> _values;
    private readonly string _prefix;

    private TableQuery(string path, Dictionary<string, StringValues> values, string defaultSort, bool defaultDescending, string prefix, int defaultPageSize)
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
        Group = Get(Key("group"));
    }

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

    /// <param name="navigation">Current address.</param>
    /// <param name="defaultSort">Column sorted by when the address names none.</param>
    /// <param name="defaultDescending">True to list the default column newest or largest first.</param>
    /// <param name="prefix">Prefix of the address values when a page has several tables, e.g. "hist."; empty for one table.</param>
    /// <param name="defaultPageSize">Rows per page when the address names none.</param>
    public static TableQuery From(NavigationManager navigation, string defaultSort, bool defaultDescending = false, string prefix = "", int defaultPageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var uri = new Uri(navigation.Uri);
        return new TableQuery(uri.AbsolutePath, QueryHelpers.ParseQuery(uri.Query), defaultSort, defaultDescending, prefix, defaultPageSize);
    }

    /// <summary>For tests and pages that build a query from known values.</summary>
    public static TableQuery Parse(string pathAndQuery, string defaultSort, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var index = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return index < 0
            ? new TableQuery(pathAndQuery, [], defaultSort, false, prefix, DefaultPageSize)
            : new TableQuery(pathAndQuery[..index], QueryHelpers.ParseQuery(pathAndQuery[index..]), defaultSort, false, prefix, DefaultPageSize);
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value.ToString() : null;

    /// <summary>Selected values of a column filter; empty means no filter.</summary>
    public IReadOnlyList<string> Filter(string column) =>
        _values.TryGetValue(FilterField(column), out var values) ? [.. values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.Ordinal)] : [];

    public IEnumerable<(string Column, string Value)> ActiveFilters =>
        _values.Where(p => p.Key.StartsWith(_prefix + FilterPrefix, StringComparison.Ordinal))
            .Select(p => p.Key[(_prefix.Length + FilterPrefix.Length)..])
            .SelectMany(column => Filter(column).Select(v => (column, v)));

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

    public string WithoutFilter(string column, string value) =>
        With(values =>
        {
            var key = FilterField(column);
            var remaining = Filter(column).Where(v => v != value).ToArray();
            if (remaining.Length == 0)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = remaining;
            }

            values.Remove(Key("page"));
        });

    public string WithFilter(string column, params string[] filterValues) =>
        With(values =>
        {
            values[FilterField(column)] = filterValues;
            values.Remove(Key("page"));
        });

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

/// <summary>One page of rows after search, filters and sorting, with the filter options and their counts.</summary>
public sealed record TablePage<T>(IReadOnlyList<T> Rows, int Matching, int Total, int Page, int Pages, IReadOnlyDictionary<string, IReadOnlyList<FilterOption>> Options, int PageSize = TableQuery.DefaultPageSize);

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
            var selected = query.Filter(column.Key);
            if (selected.Count > 0)
            {
                filtered = filtered.Where(r => column.FilterValues!(r).Any(v => selected.Contains(v, StringComparer.Ordinal)));
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

        return new TablePage<T>([.. matching.Skip((page - 1) * pageSize).Take(pageSize)], matching.Count, rows.Count, page, pages, options, pageSize);
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
