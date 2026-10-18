using System.ComponentModel.DataAnnotations;

namespace UpdateCoordinator.Server.Data;

/// <summary>
/// Location of the SQLite database. Bound from the "Database" configuration section.
/// </summary>
internal sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Full path of the database file. Defaults to %ProgramData%\UpdateCoordinator\Server\server.db.</summary>
    [Required]
    public string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UpdateCoordinator",
        "Server",
        "server.db");
}
