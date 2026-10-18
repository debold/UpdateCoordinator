using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Validates every string in a collection against a pattern and a maximum length.
/// DataAnnotations has no built-in attribute for collection items.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ItemsMatchAttribute(string pattern, int maxLength) : ValidationAttribute
{
    private readonly Regex _regex = new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public string Pattern { get; } = pattern;

    public int MaxLength { get; } = maxLength;

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is not IEnumerable items)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item is not string text || text.Length == 0 || text.Length > MaxLength || !_regex.IsMatch(text))
            {
                return false;
            }
        }

        return true;
    }

    public override string FormatErrorMessage(string name) =>
        $"Every entry of {name} must match {Pattern} and be at most {MaxLength} characters long.";
}
