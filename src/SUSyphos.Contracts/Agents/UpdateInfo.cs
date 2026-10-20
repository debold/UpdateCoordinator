using System.ComponentModel.DataAnnotations;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Common description of an update, identified uniquely by update ID and revision
/// (see docs/03-communication-protocol.md, section 6). Shared by inventory and update report.
/// </summary>
public abstract record UpdateInfo
{
    [Required]
    public required Guid UpdateId { get; init; }

    [Range(0, int.MaxValue)]
    public required int Revision { get; init; }

    /// <summary>KB number without prefix, e.g. "5034129".</summary>
    [StringLength(10)]
    [RegularExpression("^[0-9]{1,10}$")]
    public string? KbArticle { get; init; }

    [Required]
    [StringLength(512, MinimumLength = 1)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public required string Title { get; init; }

    /// <summary>Update classification, e.g. "Security Updates".</summary>
    [StringLength(64)]
    [RegularExpression(ValidationPatterns.PrintableText)]
    public string? Classification { get; init; }
}
