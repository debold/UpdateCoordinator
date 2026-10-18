namespace UpdateCoordinator.Agent.Tests;

public sealed class BackoffTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(5, 160)]
    public void Compute_DoublesPerFailure(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Backoff.Compute(failures, Interval, Max));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(1_000)]
    [InlineData(int.MaxValue)]
    public void Compute_IsCappedAtMaximum(int failures)
    {
        Assert.Equal(Max, Backoff.Compute(failures, Interval, Max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Compute_WithoutFailures_ReturnsInterval(int failures)
    {
        Assert.Equal(Interval, Backoff.Compute(failures, Interval, Max));
    }
}
