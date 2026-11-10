using System.Security.Cryptography;
using System.Text;

namespace SUSyphos.Server.Software;

/// <summary>Helpers shared by everything that maps WinGet packages onto the update catalog.</summary>
internal static class SoftwarePackages
{
    /// <summary>Namespace of the name-based update IDs of software versions (fixed, never change it).</summary>
    private static readonly Guid SoftwareNamespace = new("6f1c3a52-8d0e-4b7a-9f25-2c4e1d7b9a60");

    /// <summary>Revision of every software version in the update catalog; a version never changes, a new one is a new entry.</summary>
    public const int SoftwareRevision = 1;

    /// <summary>The category software versions are shown with next to the classifications of Windows updates.</summary>
    public const string Classification = "Software";

    /// <summary>
    /// The update ID of a software version: a name-based UUID (RFC 9562, version 5) of "winget:&lt;package id&gt;:&lt;version&gt;",
    /// with the package ID in lower case because WinGet compares it without case. The same version always gets the same ID.
    /// </summary>
    public static Guid SoftwareUpdateId(string packageId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var name = Encoding.UTF8.GetBytes($"winget:{packageId.ToLowerInvariant()}:{version}");
        Span<byte> namespaceBytes = stackalloc byte[16];
        SoftwareNamespace.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var input = new byte[16 + name.Length];
        namespaceBytes.CopyTo(input);
        name.CopyTo(input, 16);
#pragma warning disable CA5350 // SHA-1 is what RFC 9562 prescribes for version 5 UUIDs; no security depends on it.
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>Title of a software version in the update catalog, e.g. "7-Zip 26.06".</summary>
    public static string Title(string name, string version) => $"{name} {version}";

    /// <summary>
    /// Whether a package ID is valid: WinGet identifiers are segments of letters, digits and "-_+" separated by dots
    /// (at least two segments), at most 128 characters.
    /// </summary>
    public static bool IsValidId(string? packageId) =>
        packageId is { Length: > 2 and <= Data.PackageEntity.MaxIdLength }
        && packageId.Split('.') is { Length: >= 2 } segments
        && segments.All(s => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '+'));
}
