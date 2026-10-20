namespace SUSyphos.Common;

/// <summary>
/// Default data folders under %ProgramData%.
/// </summary>
public static class DataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SUSyphos");

    public static string Agent { get; } = Path.Combine(Root, "Agent");

    public static string Server { get; } = Path.Combine(Root, "Server");
}
