using SUSyphos.TestSupport;

namespace SUSyphos.UiTests;

/// <summary>
/// A second central server with the clean showcase lab (<see cref="ShowcaseData"/>) for the screenshots of the help and the
/// README, so they show realistic data instead of the edge cases the layout tests need. A collection fixture, not an
/// assembly fixture: it exists only while the screenshot tests run, so the runner never holds two servers with their
/// browsers through the whole run (CI ran out of socket buffers with both).
/// </summary>
public sealed class ShowcaseFixture : UiFixture
{
    protected override Task<Guid> SeedAsync(KestrelServerFactory server) => ShowcaseData.SeedAsync(server);
}

/// <summary>The screenshot tests share one showcase lab and run one after the other.</summary>
[CollectionDefinition(Name)]
public sealed class ShowcaseLab : ICollectionFixture<ShowcaseFixture>
{
    public const string Name = "Showcase";
}
