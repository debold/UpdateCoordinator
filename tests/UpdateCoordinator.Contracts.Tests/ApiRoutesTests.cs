using System.Reflection;

namespace UpdateCoordinator.Contracts.Tests;

public sealed class ApiRoutesTests
{
    private static readonly string[] Routes = typeof(ApiRoutes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void Routes_AreDefined()
    {
        Assert.NotEmpty(Routes);
    }

    [Fact]
    public void Routes_AreAbsoluteLowercaseAndWithoutWhitespace()
    {
        Assert.All(Routes, route =>
        {
            Assert.StartsWith("/", route, StringComparison.Ordinal);
            Assert.Equal(route.ToLowerInvariant(), route);
            Assert.DoesNotContain(route, char.IsWhiteSpace);
        });
    }

    [Fact]
    public void Routes_AreUnique()
    {
        Assert.Equal(Routes.Length, Routes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
