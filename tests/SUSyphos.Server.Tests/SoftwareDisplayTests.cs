using SUSyphos.Server.Components.Pages;
using SUSyphos.Server.Software;

namespace SUSyphos.Server.Tests;

/// <summary>How versions and tags of software are shown (Marc, 2026-11-13).</summary>
public sealed class SoftwareDisplayTests
{
    [Fact]
    public void Versions_ThatAreTheSame_AreShownOnce_InTheShorterSpelling()
    {
        Assert.Equal(["26.06"], SoftwareOverview.Distinct(["26.06.00.0", "26.06"]));
        Assert.Equal(["26.06", "24.09"], SoftwareOverview.Distinct(["24.09", "26.06.00.0", "26.06"]));
    }

    [Fact]
    public void Tags_ThatDifferOnlyInSeparators_AreShownOnce()
    {
        Assert.Equal(["microsoft-iis-url-rewrite", "iis"], PackageDetail.DistinctTags("microsoft-iis-url-rewrite, microsoftiisurlrewrite, iis, IIS"));
    }
}
