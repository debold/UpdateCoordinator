using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// A program installed on the server (docs/05-agent-design.md, software inventory): one entry of "Programs and Features"
/// (uninstall keys of the machine) or a packaged app, with the WinGet package WinGet correlated it with, if any.
/// </summary>
public sealed record InstalledSoftware
{
    /// <summary>Upper bound for programs per inventory.</summary>
    public const int MaxEntries = 2_000;

    /// <summary>WinGet package identifier: dot-separated segments of letters, digits and "-_+".</summary>
    public const string PackageIdPattern = @"^[A-Za-z0-9\-_+]+(?:\.[A-Za-z0-9\-_+]+)+$";

    /// <summary>Version as programs write it: letters, digits and ". -_+()", no spaces at the ends.</summary>
    public const string VersionPattern = @"^[A-Za-z0-9](?:[A-Za-z0-9.\-_+() ]{0,62}[A-Za-z0-9)])?$";

    [Required]
    public required SoftwareView View { get; init; }

    /// <summary>Name of the uninstall key (e.g. a product code), or the package family name of a packaged app.</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public required string KeyName { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public required string DisplayName { get; init; }

    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(VersionPattern)]
    public string? DisplayVersion { get; init; }

    [StringLength(256, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? Publisher { get; init; }

    /// <summary>The WinGet package WinGet correlated the program with; null when WinGet knows none or was not asked.</summary>
    [StringLength(128, MinimumLength = 3)]
    [RegularExpression(PackageIdPattern)]
    public string? PackageId { get; init; }

    /// <summary>The installed version in WinGet's notation; may differ from <see cref="DisplayVersion"/>.</summary>
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(VersionPattern)]
    public string? InstalledVersion { get; init; }
}

/// <summary>Where an installed program was found.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SoftwareView>))]
public enum SoftwareView
{
    /// <summary>Uninstall keys, 64-bit registry view.</summary>
    Machine64,

    /// <summary>Uninstall keys, 32-bit registry view (WOW6432Node).</summary>
    Machine32,

    /// <summary>A packaged app (MSIX) installed for the machine.</summary>
    Msix,
}

/// <summary>Whether SUSyphos can manage software with WinGet on the server, and why not.</summary>
public sealed record SoftwareSupport
{
    [Required]
    public required SoftwareSupportState State { get; init; }

    /// <summary>Why WinGet is not supported or failed, e.g. "Server Core" or the error of the engine; null when supported.</summary>
    [StringLength(256, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? Detail { get; init; }
}

/// <summary>
/// What the WinGet helper of the agent (SUSyphos.Agent.WinGet.exe inventory) writes to its standard output. Internal
/// between agent and helper; the agent sends its parts with the inventory.
/// </summary>
public sealed record SoftwareInventoryReport
{
    /// <summary>JSON settings of the document (web defaults; the enums carry their own converters).</summary>
    public static System.Text.Json.JsonSerializerOptions JsonOptions { get; } = new(System.Text.Json.JsonSerializerDefaults.Web);

    public required IReadOnlyList<InstalledSoftware> Software { get; init; }

    public required SoftwareSupport Support { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SoftwareSupportState>))]
public enum SoftwareSupportState
{
    /// <summary>WinGet works: installed programs are matched with packages, and software can be installed.</summary>
    Supported,

    /// <summary>The server cannot use WinGet (Windows Server 2016, Server Core); only the installed programs are reported.</summary>
    NotSupported,

    /// <summary>WinGet should work but failed this time (engine not loaded, timeout); only the installed programs are reported.</summary>
    Failed,
}
