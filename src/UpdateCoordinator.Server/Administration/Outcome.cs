namespace UpdateCoordinator.Server.Administration;

/// <summary>Result of an administrative change: null on success, otherwise the reason it was refused.</summary>
internal sealed record Outcome(string? Error)
{
    public static Outcome Ok { get; } = new((string?)null);

    public bool Succeeded => Error is null;
}
