namespace UpdateCoordinator.Common;

/// <summary>
/// Default data folders under %ProgramData%. The product name is a placeholder until the project name is decided.
/// </summary>
public static class DataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UpdateCoordinator");

    public static string Agent { get; } = Path.Combine(Root, "Agent");

    public static string Server { get; } = Path.Combine(Root, "Server");
}
