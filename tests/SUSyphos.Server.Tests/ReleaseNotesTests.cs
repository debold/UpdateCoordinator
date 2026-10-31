using SUSyphos.Server.AgentPackages;

namespace SUSyphos.Server.Tests;

/// <summary>Release notes of the GitHub releases: kept by the check, shown without HTML, scripts or foreign links.</summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public void Markdown_BecomesHtml_WithSafeLinksInANewTab()
    {
        var html = ReleaseNotes.ToHtml("### Agent\n\n- New **view** menu\n- See [release](https://github.com/debold/UpdateCoordinator/releases)");

        Assert.Contains("<h3", html, StringComparison.Ordinal);
        Assert.Contains("<li>New <strong>view</strong> menu</li>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://github.com/debold/UpdateCoordinator/releases\"", html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "<img")]
    [InlineData("[click](javascript:alert(1))", "javascript:")]
    [InlineData("[plain](http://example.com/x)", "href=\"http://example.com")]
    [InlineData("![pixel](https://tracker.example/p.png)", "<img")]
    [InlineData("<javascript:alert(1)>", "href=\"javascript:")]
    [InlineData("<mail@example.com>", "mailto:")]
    public void Html_ScriptsImagesAndForeignLinks_AreNeverRendered(string markdown, string forbidden) =>
        Assert.DoesNotContain(forbidden, ReleaseNotes.ToHtml(markdown), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void UnsafeLink_KeepsItsText() =>
        Assert.Contains("click", ReleaseNotes.ToHtml("[click](javascript:alert(1))"), StringComparison.Ordinal);

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" text ", "text")]
    public void Limit_TrimsAndDropsEmptyNotes(string? notes, string? expected) =>
        Assert.Equal(expected, ReleaseNotes.Limit(notes));

    [Fact]
    public void Limit_CutsLongNotes()
    {
        var limited = ReleaseNotes.Limit(new string('a', ReleaseNotes.MaxLength + 500))!;

        Assert.True(limited.Length < ReleaseNotes.MaxLength + 10);
        Assert.EndsWith("…", limited, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubRelease_CarriesItsBody()
    {
        var release = new GitHubAgentReleases.GitHubRelease(
            "release/v1.4.0",
            false,
            false,
            [
                new("SUSyphos.Agent-1.4.0.msi", new Uri("https://github.com/debold/UpdateCoordinator/releases/download/release/v1.4.0/SUSyphos.Agent-1.4.0.msi")),
                new("SHA256SUMS.txt", new Uri("https://github.com/debold/UpdateCoordinator/releases/download/release/v1.4.0/SHA256SUMS.txt")),
            ],
            null,
            "### Agent\n- something new");

        var parsed = Assert.Single(GitHubAgentReleases.Parse([release], "debold/UpdateCoordinator"));

        Assert.Equal("### Agent\n- something new", parsed.Notes);
    }
}
