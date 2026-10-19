namespace UpdateCoordinator.Server.Components;

/// <summary>Position of a page in a filtered list, for the pager component.</summary>
public sealed record PageInfo(int Page, int Pages, int Matching, int Total, int PageSize = TableQuery.PageSize)
{
    public static PageInfo Of<T>(TablePage<T> page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PageInfo(page.Page, page.Pages, page.Matching, page.Total);
    }
}
