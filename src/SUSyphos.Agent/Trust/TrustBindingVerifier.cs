using System.Security.Cryptography;

using SUSyphos.Contracts.Trust;

namespace SUSyphos.Agent.Trust;

internal enum TrustDecision
{
    Accepted,
    MalformedBinding,
    AnchorMismatch,
    InvalidSignature,
    CertificateMismatch,
    OutdatedSerial,

    /// <summary>First contact, but the anchor does not match the fingerprint from the installer file name.</summary>
    FingerprintMismatch,
}

/// <summary>
/// Decides whether a binding fetched from the central server may be trusted (see docs/03-communication-protocol.md, 3.2).
/// Pure function without side effects, so every attack case can be tested directly.
/// </summary>
internal static class TrustBindingVerifier
{
    /// <param name="binding">Binding as received; untrusted input.</param>
    /// <param name="presentedCertificateSpkiSha256">Key hash of the TLS certificate the server actually presented.</param>
    /// <param name="state">Current trust state; without a pinned anchor the binding's anchor is pinned (trust on first use).</param>
    /// <param name="newState">Updated state if the binding is accepted, otherwise the unchanged state.</param>
    /// <param name="expectedAnchorFingerprint">
    /// Fingerprint from the installer file name (docs/03-communication-protocol.md, 3.3); checked only when an anchor is pinned,
    /// so the first contact is no longer trust on first use.
    /// </param>
    public static TrustDecision Verify(
        TrustBinding? binding,
        ReadOnlySpan<byte> presentedCertificateSpkiSha256,
        TrustState state,
        out TrustState newState,
        string? expectedAnchorFingerprint = null)
    {
        newState = state;
        if (binding is null
            || binding.Serial <= 0
            || !TryDecode(binding.AnchorPublicKey, TrustBindingFormat.MaxEncodedLength, out var anchor)
            || !TryDecode(binding.CertificateSpkiSha256, TrustBindingFormat.MaxEncodedLength, out var certificateHash)
            || certificateHash.Length != TrustBindingFormat.HashLength
            || !TryDecode(binding.Signature, TrustBindingFormat.MaxEncodedLength, out var signature))
        {
            return TrustDecision.MalformedBinding;
        }

        if (state.AnchorPublicKey is not null
            && !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(state.AnchorPublicKey), anchor))
        {
            return TrustDecision.AnchorMismatch;
        }

        if (state.AnchorPublicKey is null
            && !string.IsNullOrWhiteSpace(expectedAnchorFingerprint)
            && !string.Equals(TrustBindingFormat.ComputeAnchorFingerprint(anchor), expectedAnchorFingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return TrustDecision.FingerprintMismatch;
        }

        if (!IsValidSignature(anchor, TrustBindingFormat.GetSignedBytes(binding.Serial, certificateHash), signature))
        {
            return TrustDecision.InvalidSignature;
        }

        if (!presentedCertificateSpkiSha256.SequenceEqual(certificateHash))
        {
            return TrustDecision.CertificateMismatch;
        }

        var sameAsAccepted = state.AcceptedCertificateSpkiSha256 == binding.CertificateSpkiSha256;
        if (binding.Serial < state.HighestSerial || (binding.Serial == state.HighestSerial && !sameAsAccepted))
        {
            return TrustDecision.OutdatedSerial;
        }

        newState = state with
        {
            AnchorPublicKey = Convert.ToBase64String(anchor),
            HighestSerial = binding.Serial,
            AcceptedCertificateSpkiSha256 = Convert.ToBase64String(certificateHash),
        };
        return TrustDecision.Accepted;
    }

    private static bool IsValidSignature(byte[] anchor, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(anchor, out var read);
            return read == anchor.Length
                && key.KeySize == 256
                && key.VerifyData(data, signature, TrustBindingFormat.HashAlgorithm);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryDecode(string? value, int maxLength, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        var buffer = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return written > 0;
    }
}
