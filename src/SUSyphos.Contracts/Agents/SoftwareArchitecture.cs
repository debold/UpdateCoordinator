using System.Text.RegularExpressions;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// The processor architecture a program names in its display name ("Microsoft Windows Desktop Runtime 10.0.12 (x64)").
/// Burn bundles such as the .NET runtimes and the Visual C++ Redistributable register their x64 build in the 32-bit
/// view of the registry, so the name is the more reliable hint (lab, 2026-11-16: the x86 runtime was installed next to
/// the x64 one it should have updated).
/// </summary>
public static partial class SoftwareArchitecture
{
    public const string X64 = "x64";
    public const string X86 = "x86";
    public const string Arm64 = "arm64";

    /// <summary>"x64", "x86", "arm64" from the name, or null when the name does not tell.</summary>
    public static string? FromName(string? name) =>
        name is null ? null
            : Arm64Pattern().IsMatch(name) ? Arm64
            : X64Pattern().IsMatch(name) ? X64
            : X86Pattern().IsMatch(name) ? X86
            : null;

    /// <summary>True for the values the release list may carry.</summary>
    public static bool IsKnown(string? value) => value is X64 or X86 or Arm64;

    [GeneratedRegex(@"(?<![A-Za-z0-9])arm64(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Arm64Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(x64|amd64|64-bit|x86_64)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex X64Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(x86|32-bit|win32)(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex X86Pattern();
}
