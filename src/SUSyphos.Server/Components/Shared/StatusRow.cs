using Microsoft.AspNetCore.Components;

namespace SUSyphos.Server.Components.Shared;

/// <summary>One row of a <see cref="StatusList"/>.</summary>
/// <param name="Kind">"fail", "queue", "pend", "ok" or "neutral", as for pills.</param>
/// <param name="Pill">The state in a word or two.</param>
/// <param name="Name">What the row is about.</param>
/// <param name="Text">The short value.</param>
/// <param name="Explanation">Muted explanation after " · "; never part of the value's sentence.</param>
/// <param name="Action">Link or chips at the right edge; text links end with "→".</param>
/// <param name="Extra">Further content after the value (for example a fix with its own controls).</param>
/// <param name="PillDetail">Detail in the "i" segment of the pill.</param>
public sealed record StatusRow(string Kind, string Pill, string Name, string Text, string? Explanation = null, RenderFragment? Action = null, RenderFragment? Extra = null, string? PillDetail = null)
{
    /// <summary>Sort order: attention first.</summary>
    public static int Order(string kind) => kind switch
    {
        "fail" => 0,
        "queue" => 1,
        "pend" => 2,
        "ok" => 3,
        _ => 4,
    };
}