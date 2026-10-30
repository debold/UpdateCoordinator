using SUSyphos.Server.Components;

namespace SUSyphos.Server.Tests;

/// <summary>Views of a table: fixed sets of column filters, the view name derived from the filters, exclusions in the address.</summary>
public sealed class TableViewTests
{
    private sealed record Item(string Name, string State, string Kind);

    private static readonly IReadOnlyList<TableColumn<Item>> Columns =
    [
        new("name", "Name", i => i.Name),
        new("state", "State", i => i.State, i => [i.State]),
        new("kind", "Kind", i => i.Kind, i => [i.Kind]),
    ];

    private static readonly List<Item> Items =
    [
        new("a", "Open", "Security"), new("b", "Open", "Driver"), new("c", "Failed", "Security"),
        new("d", "Done", "Security"), new("e", "Superseded", "Driver"), new("f", "Waiting", "Driver"),
    ];

    private static readonly IReadOnlyList<TableView> Views =
    [
        new("open", "Open", "Not done or superseded", ("state", TableFilter.Not("Done", "Superseded"))),
        new("problems", "Problems", "Failed", ("state", TableFilter.In("Failed"))),
        new("security", "Open security", "Open and security", ("state", TableFilter.Not("Done", "Superseded")), ("kind", TableFilter.In("Security"))),
        new("all", "All", "No filter"),
    ];

    private static TablePage<Item> Page(string address) =>
        TablePaging.Apply(Items, TableQuery.Parse(address, "name", views: Views), Columns, i => i.Name);

    [Fact]
    public void NoParameter_ShowsTheDefaultView()
    {
        var page = Page("/x");

        Assert.Equal(["a", "b", "c", "f"], page.Rows.Select(r => r.Name));
        Assert.Same(Views[0], page.View!.Current);
        Assert.True(page.View.IsDefault);
    }

    [Theory]
    [InlineData("problems", new[] { "c" })]
    [InlineData("security", new[] { "a", "c" })]
    [InlineData("all", new[] { "a", "b", "c", "d", "e", "f" })]
    public void ViewParameter_SetsTheFiltersOfTheView(string key, string[] names)
    {
        var page = Page($"/x?view={key}");

        Assert.Equal(names, page.Rows.Select(r => r.Name));
        Assert.Equal(key, page.View!.Current!.Key);
        Assert.False(page.View.IsDefault);
    }

    [Fact]
    public void Menu_CountsTheRowsOfEveryView_WithoutTheSearch()
    {
        var page = Page("/x?q=a");

        Assert.Equal(new Dictionary<string, int> { ["open"] = 4, ["problems"] = 1, ["security"] = 2, ["all"] = 6 }, page.View!.Counts);
    }

    [Fact]
    public void ExclusionInTheAddress_HidesOnlyTheseValues()
    {
        var page = Page("/x?f.state=!Done&f.state=!Failed");

        Assert.Equal(["a", "b", "e", "f"], page.Rows.Select(r => r.Name));
        Assert.Null(page.View!.Current);
    }

    [Fact]
    public void FiltersSetByHand_ThatMatchAView_ShowItsName()
    {
        // A form sends the checked values; every state but Done and Superseded is the default view.
        var page = Page("/x?f.state=Open&f.state=Failed&f.state=Waiting");

        Assert.Same(Views[0], page.View!.Current);
        Assert.Equal(TableFilter.Not("Done", "Superseded").Values, page.View.Filters["state"].Values);
        Assert.True(page.View.Filters["state"].Exclude);
    }

    [Fact]
    public void ChangedFilter_IsCustom()
    {
        var page = Page("/x?f.state=!Done");

        Assert.Null(page.View!.Current);
        Assert.False(page.View.IsDefault);
        Assert.Equal(["a", "b", "c", "e", "f"], page.Rows.Select(r => r.Name));
    }

    [Fact]
    public void RemovingTheFilterOfTheDefaultView_ShowsEverything_AndNamesTheViewAll()
    {
        var query = TableQuery.Parse("/x", "name", views: Views);
        var link = query.WithFilter("state", null);

        Assert.Equal("/x?view=custom", link);
        var page = Page(link);
        Assert.Equal(6, page.Rows.Count);
        Assert.Equal("all", page.View!.Current!.Key);
    }

    [Fact]
    public void ChipLink_KeepsTheOtherFiltersOfTheView_AndDropsTheView()
    {
        var query = TableQuery.Parse("/x?view=security&q=x", "name", views: Views);
        var link = query.WithFilter("kind", null);

        Assert.Equal("/x?f.state=%21Done&f.state=%21Superseded&q=x", link);
        Assert.Same(Views[0], Page(link).View!.Current);
    }

    [Fact]
    public void ViewLink_ReplacesTheFilters_AndTheDefaultNeedsNoParameter()
    {
        var query = TableQuery.Parse("/x?f.state=Failed&page=2&sort=name", "name", views: Views);

        Assert.Equal("/x?view=problems&sort=name", query.ViewLink(Views[1]));
        Assert.Equal("/x?sort=name", query.ViewLink(Views[0]));
    }

    [Fact]
    public void FilterForm_CarriesTheOtherFiltersOfTheView()
    {
        var query = TableQuery.Parse("/x?view=security", "name", views: Views);
        var fields = query.FilterFormFields("kind").ToList();

        Assert.Contains(fields, f => f.Key == "f.state" && f.Value == "!Done");
        Assert.Contains(fields, f => f.Key == "f.state" && f.Value == "!Superseded");
        Assert.Contains(fields, f => f.Key == "view" && f.Value == TableQuery.CustomView);
        Assert.DoesNotContain(fields, f => f.Key == "f.kind");
    }

    [Theory]
    [InlineData("/x?view=nonsense")]
    [InlineData("/x?view=%3Cscript%3E")]
    public void UnknownView_FallsBackToTheDefault(string address) =>
        Assert.Same(Views[0], Page(address).View!.Current);

    [Fact]
    public void TableWithoutViews_HasNoViewState()
    {
        var page = TablePaging.Apply(Items, TableQuery.Parse("/x?view=open", "name"), Columns, i => i.Name);

        Assert.Null(page.View);
        Assert.Equal(6, page.Rows.Count);
    }

    [Theory]
    [InlineData(new[] { "Open", "Failed", "Waiting", "Done" }, true, new[] { "Superseded" })]
    [InlineData(new[] { "Open" }, false, new[] { "Open" })]
    public void Shortest_IsAnExclusion_WhenMoreThanHalfIsChecked(string[] selected, bool exclude, string[] values)
    {
        var filter = TableFilter.Of(selected, ["Done", "Failed", "Open", "Superseded", "Waiting"])!;

        Assert.Equal(exclude, filter.Exclude);
        Assert.Equal(values, filter.Values);
    }

    [Fact]
    public void Shortest_IsNoFilter_WhenEverythingIsChecked() =>
        Assert.Null(TableFilter.Of(["a", "b"], ["a", "b"]));

    [Fact]
    public void Exclusion_KeepsRowsWithoutAValue() =>
        Assert.True(TableFilter.Not("x").Matches([]));
}
