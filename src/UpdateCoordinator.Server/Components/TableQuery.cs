using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace UpdateCoordinator.Server.Components;

/// <summary>
/// State of a dashboard table taken from the address: search (<c>q</c>), sort (<c>sort</c>, <c>dir</c>), page
/// (<c>page</c>), grouping (<c>group</c>) and column filters (<c>f.&lt;column&gt;</c>, repeatable). Every view is a
/// link that can be bookmarked and shared, and no script is needed. Values are only compared with known values,
/// never interpreted, so an unknown or manipulated value simply matches nothing.
/// </summary>
public sealed class TableQuery
{
    public const int PageSize = 25;
    public const int MaxSearchLength = 200;
    private const string FilterPrefix = "f.";

    private readonly string _path;
    private readonly Dictionary<string, StringValues> _values;

    private TableQuery(string path, Dictionary<string, StringValues> values, string defaultSort)
    {
        _path = path;
        _values = values;
        var sort = Get("sort");
        Sort = string.IsNullOrEmpty(sort) ? defaultSort : sort;
        Descending = Get("dir") == "desc";
        Page = int.TryParse(Get("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1;
        var search = Get("q")?.Trim();
        Search = string.IsNullOrEmpty(search) ? null : search.Length > MaxSearchLength ? search[..MaxSearchLength] : search;
        Group = Get("group");
    }

    public string? Search { get; }

    public string Sort { get; }

    public bool Descending { get; }

    public int Page { get; }

    public string? Group { get; }

    public static TableQuery From(NavigationManager navigation, string defaultSort)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var uri = new Uri(navigation.Uri);
        return new TableQuery(uri.AbsolutePath, QueryHelpers.ParseQuery(uri.Query), defaultSort);
    }

    /// <summary>For tests and pages that build a query from known values.</summary>
    public static TableQuery Parse(string pathAndQuery, string defaultSort)
    {
        var index = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return index < 0
            ? new TableQuery(pathAndQuery, [], defaultSort)
            : new TableQuery(pathAndQuery[..index], QueryHelpers.ParseQuery(pathAndQuery[index..]), defaultSort);
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value.ToString() : null;

    /// <summary>Selected values of a column filter; empty means no filter.</summary>
    public IReadOnlyList<string> Filter(string column) =>
        _values.TryGetValue(FilterPrefix + column, out var values) ? [.. values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.Ordinal)] : [];

    public IEnumerable<(string Column, string Value)> ActiveFilters =>
        _values.Where(p => p.Key.StartsWith(FilterPrefix, StringComparison.Ordinal))
            .SelectMany(p => Filter(p.Key[FilterPrefix.Length..]).Select(v => (p.Key[FilterPrefix.Length..], v)));

    public static string FilterField(string column) => FilterPrefix + column;

    public string SortLink(string column) =>
        With(values =>
        {
            var descending = Sort == column && !Descending;
            values["sort"] = column;
            values["dir"] = descending ? "desc" : "asc";
            values.Remove("page");
        });

    public string PageLink(int page) => With(values => values["page"] = page.ToString(CultureInfo.InvariantCulture));

    public string WithoutFilter(string column, string value) =>
        With(values =>
        {
            var key = FilterPrefix + column;
            var remaining = Filter(column).Where(v => v != value).ToArray();
            if (remaining.Length == 0)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = remaining;
            }

            values.Remove("page");
        });

    public string WithFilter(string column, params string[] filterValues) =>
        With(values =>
        {
            values[FilterPrefix + column] = filterValues;
            values.Remove("page");
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

    /// <summary>Hidden fields that keep the rest of the view when a GET form changes some of it.</summary>
    public IEnumerable<KeyValuePair<string, string>> HiddenFieldsExcept(params string[] keys) =>
        _values.Where(p => !keys.Contains(p.Key) && p.Key != "page")
            .SelectMany(p => p.Value.Where(v => v is not null).Select(v => new KeyValuePair<string, string>(p.Key, v!)));

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
public sealed record TableColumn<T>(string Key, string Label, Func<T, IComparable?> SortKey, Func<T, IReadOnlyList<string>>? FilterValues = null)
{
    /// <summary>Group a row belongs to when the list is grouped by this column: its first filter value.</summary>
    public string GroupKey(T row) => FilterValues?.Invoke(row) is [var first, ..] ? first : string.Empty;
}

/// <summary>One page of rows after search, filters and sorting, with the filter options and their counts.</summary>
public sealed record TablePage<T>(IReadOnlyList<T> Rows, int Matching, int Total, int Page, int Pages, IReadOnlyDictionary<string, IReadOnlyList<FilterOption>> Options);

public sealed record FilterOption(string Value, int Count);

public static class TablePaging
{
    /// <summary>Applies a table query to rows in memory (the dashboard handles thousands of servers, not millions).</summary>
    public static TablePage<T> Apply<T>(IReadOnlyList<T> rows, TableQuery query, IReadOnlyList<TableColumn<T>> columns, Func<T, string> searchText, int pageSize = TableQuery.PageSize)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);
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

        var sort = columns.FirstOrDefault(c => c.Key == query.Sort) ?? columns[0];

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

        return new TablePage<T>([.. matching.Skip((page - 1) * pageSize).Take(pageSize)], matching.Count, rows.Count, page, pages, options);
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
