using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SUSyphos.Contracts.Trust;

namespace SUSyphos.Contracts.Scripts;

/// <summary>
/// What the trust anchor and the script key sign for scripts (Marc, 2026-11-13), defined once so central server and agent
/// cannot drift apart. Same conventions as <see cref="TrustBindingFormat"/>: ASCII text with a purpose prefix and "|"
/// separators, SHA-256, ECDSA signatures in IEEE P1363 format. The chain is anchor → script key → revision.
/// </summary>
public static partial class ScriptSigningFormat
{
    private const string KeyPurpose = "SUSyphos.ScriptKey.v1";
    private const string ScriptPurpose = "SUSyphos.Script.v1";

    /// <summary>Bytes of the key certificate the trust anchor signs.</summary>
    public static byte[] KeyCertificateBytes(string keyId, ReadOnlySpan<byte> keySubjectPublicKeyInfo, DateTimeOffset createdAt)
    {
        CheckId(keyId);
        return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{KeyPurpose}|{keyId}|{Convert.ToHexString(SHA256.HashData(keySubjectPublicKeyInfo))}|{createdAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"));
    }

    /// <summary>
    /// Bytes of an approved revision the script key signs. PowerShell 7 adds "|pwsh7" at the end; a revision for
    /// Windows PowerShell 5.1 signs the same bytes as before 0.9.0-rc.7, so older revisions stay valid and an older agent
    /// refuses a PowerShell 7 revision instead of running it in 5.1 (its bytes do not match the signature). Files (0.9.0-rc.8)
    /// add "|files:" and the SHA-256 of their ZIP, so an agent before rc.8 refuses such a revision instead of running it
    /// without its files.
    /// </summary>
    public static byte[] RevisionBytes(int scriptId, int revision, string packageId, ScriptPhase phase, ScriptRunOn runOn, ScriptOnFailure onFailure, int timeoutSeconds, string contentSha256, string parametersSha256, DateTimeOffset signedAt, ScriptPowerShell powerShell = ScriptPowerShell.WindowsPowerShell, string? filesSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        if (packageId.Contains('|', StringComparison.Ordinal) || packageId.Any(c => c > 127 || char.IsControl(c)))
        {
            throw new ArgumentException("The package ID must be printable ASCII without '|'.", nameof(packageId));
        }

        return Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{ScriptPurpose}|{scriptId}|{revision}|{packageId.ToLowerInvariant()}|{phase}|{runOn}|{onFailure}|{timeoutSeconds}|{contentSha256.ToUpperInvariant()}|{parametersSha256.ToUpperInvariant()}|{signedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}{(powerShell == ScriptPowerShell.PowerShell7 ? "|pwsh7" : string.Empty)}{(filesSha256 is null ? string.Empty : "|files:" + filesSha256.ToUpperInvariant())}"));
    }

    /// <summary>
    /// The bytes a script is stored, signed and run as: UTF-8 with BOM and CRLF line ends (PowerShell 5.1 reads a file
    /// without BOM in the system code page and would garble umlauts).
    /// </summary>
    public static byte[] NormalizeContent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var crlf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        return [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(false).GetBytes(crlf.TrimStart('﻿'))];
    }

    /// <summary>The text of stored script bytes (without the BOM).</summary>
    public static string ContentText(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var preamble = Encoding.UTF8.GetPreamble();
        return new UTF8Encoding(false, true).GetString(content.AsSpan().StartsWith(preamble) ? content[preamble.Length..] : content);
    }

    /// <summary>SHA-256 as upper-case hex.</summary>
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>SHA-256 of the parameter definitions as signed (their JSON, UTF-8).</summary>
    public static string ParametersSha256(string parametersJson) => Sha256(Encoding.UTF8.GetBytes(parametersJson ?? throw new ArgumentNullException(nameof(parametersJson))));

    /// <summary>The parameter definitions as canonical JSON, the form that is signed.</summary>
    public static string ParametersJson(IEnumerable<ScriptParameter> parameters) => JsonSerializer.Serialize(parameters.ToArray(), JsonOptions);

    public static IReadOnlyList<ScriptParameter> ReadParameters(string json) => JsonSerializer.Deserialize<ScriptParameter[]>(json, JsonOptions) ?? [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A valid parameter name: a letter, then letters, digits or underscores, at most 64 characters.</summary>
    public static bool IsParameterName(string? name) => name is not null && ParameterName().IsMatch(name);

    /// <summary>
    /// Verifies a downloaded revision against the pinned trust anchor and what the release list promised: the anchor
    /// certified the key, the key signed the revision, the content and parameters hash as signed and as promised, and the
    /// package and phase are the expected ones. Returns null when everything holds, otherwise the reason.
    /// </summary>
    public static string? Verify(ScriptPackage package, ReadOnlySpan<byte> anchorSubjectPublicKeyInfo, ScriptReference expected, string expectedPackageId)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            if (package.ScriptId != expected.ScriptId || package.Revision != expected.Revision || package.Phase != expected.Phase)
            {
                return "The script is not the one the release names.";
            }

            if (!string.Equals(package.PackageId, expectedPackageId, StringComparison.OrdinalIgnoreCase))
            {
                return "The script belongs to another package.";
            }

            var keySpki = Convert.FromBase64String(package.KeyPublicKey);
            using (var anchor = ECDsa.Create())
            {
                anchor.ImportSubjectPublicKeyInfo(anchorSubjectPublicKeyInfo, out _);
                if (!anchor.VerifyData(KeyCertificateBytes(package.KeyId, keySpki, package.KeyCreatedAt), Convert.FromBase64String(package.KeyCertificate), TrustBindingFormat.HashAlgorithm))
                {
                    return "The script key is not certified by the trust anchor of the central server.";
                }
            }

            var content = Convert.FromBase64String(package.Content);
            var contentHash = Sha256(content);
            var parametersHash = ParametersSha256(package.ParametersJson);
            if (!string.Equals(contentHash, expected.ContentSha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(parametersHash, expected.ParametersSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "The script differs from the one the release names.";
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(keySpki, out _);
            if (package.FilesSha256 is { } files && (files.Length != 64 || !files.All(Uri.IsHexDigit)))
            {
                return "The SHA-256 of the files of the script is malformed.";
            }

            var signed = RevisionBytes(package.ScriptId, package.Revision, package.PackageId, package.Phase, package.RunOn, package.OnFailure, package.TimeoutSeconds, contentHash, parametersHash, package.SignedAt, package.PowerShell, package.FilesSha256);
            return key.VerifyData(signed, Convert.FromBase64String(package.Signature), TrustBindingFormat.HashAlgorithm) ? null : "The signature of the script does not verify.";
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return "The script or its signature is malformed.";
        }
    }

    private static void CheckId(string keyId)
    {
        if (keyId is null || !KeyIdPattern().IsMatch(keyId))
        {
            throw new ArgumentException("The key ID must be 8 to 64 letters, digits or hyphens.", nameof(keyId));
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.None, 1000)]
    private static partial Regex ParameterName();

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$", RegexOptions.None, 1000)]
    private static partial Regex KeyIdPattern();
}
