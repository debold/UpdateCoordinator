using System.Security.Cryptography;

using UpdateCoordinator.Agent.Trust;
using UpdateCoordinator.Contracts.Trust;

namespace UpdateCoordinator.Agent.Tests;

/// <summary>
/// Creates bindings the way the central server does, with a test anchor key.
/// </summary>
internal sealed class TestAnchor : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public static byte[] RandomCertificateHash() => RandomNumberGenerator.GetBytes(TrustBindingFormat.HashLength);

    public TrustBinding Sign(long serial, byte[] certificateHash) => new(
        PublicKey,
        Convert.ToBase64String(certificateHash),
        serial,
        Convert.ToBase64String(_key.SignData(TrustBindingFormat.GetSignedBytes(serial, certificateHash), HashAlgorithmName.SHA256)));

    public void Dispose() => _key.Dispose();
}

public sealed class TrustBindingVerifierTests : IDisposable
{
    private readonly TestAnchor _anchor = new();
    private readonly byte[] _certificate = TestAnchor.RandomCertificateHash();

    [Fact]
    public void FirstContact_PinsAnchorAndAcceptsCertificate()
    {
        var decision = TrustBindingVerifier.Verify(_anchor.Sign(1, _certificate), _certificate, TrustState.Empty, out var state);

        Assert.Equal(TrustDecision.Accepted, decision);
        Assert.Equal(_anchor.PublicKey, state.AnchorPublicKey);
        Assert.Equal(1, state.HighestSerial);
        Assert.Equal(Convert.ToBase64String(_certificate), state.AcceptedCertificateSpkiSha256);
    }

    [Fact]
    public void FirstContact_WithMatchingFingerprint_IsAccepted()
    {
        var fingerprint = TrustBindingFormat.ComputeAnchorFingerprint(Convert.FromBase64String(_anchor.PublicKey));

        Assert.Equal(TrustDecision.Accepted, TrustBindingVerifier.Verify(_anchor.Sign(1, _certificate), _certificate, TrustState.Empty, out _, fingerprint.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("not a fingerprint")]
    public void FirstContact_WithOtherFingerprint_IsRejected(string fingerprint)
    {
        // An attacker in the network at the first contact cannot get his anchor pinned when the download carried the fingerprint.
        var decision = TrustBindingVerifier.Verify(_anchor.Sign(1, _certificate), _certificate, TrustState.Empty, out var state, fingerprint);

        Assert.Equal(TrustDecision.FingerprintMismatch, decision);
        Assert.Null(state.AnchorPublicKey);
    }

    [Fact]
    public void PinnedAnchor_IgnoresFingerprint()
    {
        Assert.Equal(TrustDecision.Accepted, TrustBindingVerifier.Verify(_anchor.Sign(2, _certificate), _certificate, Pin(serial: 1), out _, "0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void NewCertificateWithHigherSerial_IsAccepted()
    {
        var pinned = Pin(serial: 1);
        var newCertificate = TestAnchor.RandomCertificateHash();

        var decision = TrustBindingVerifier.Verify(_anchor.Sign(2, newCertificate), newCertificate, pinned, out var state);

        Assert.Equal(TrustDecision.Accepted, decision);
        Assert.Equal(2, state.HighestSerial);
    }

    [Fact]
    public void SameBindingAgain_IsAccepted()
    {
        var pinned = Pin(serial: 3);

        Assert.Equal(TrustDecision.Accepted, TrustBindingVerifier.Verify(_anchor.Sign(3, _certificate), _certificate, pinned, out _));
    }

    [Fact]
    public void BindingFromOtherAnchor_IsRejected()
    {
        var pinned = Pin(serial: 1);
        using var attacker = new TestAnchor();

        var decision = TrustBindingVerifier.Verify(attacker.Sign(5, _certificate), _certificate, pinned, out var state);

        Assert.Equal(TrustDecision.AnchorMismatch, decision);
        Assert.Same(pinned, state);
    }

    [Fact]
    public void ForgedSignature_IsRejected()
    {
        var binding = _anchor.Sign(1, _certificate) with { Signature = Convert.ToBase64String(new byte[64]) };

        Assert.Equal(TrustDecision.InvalidSignature, TrustBindingVerifier.Verify(binding, _certificate, TrustState.Empty, out _));
    }

    [Fact]
    public void TamperedSerial_IsRejected()
    {
        var binding = _anchor.Sign(1, _certificate) with { Serial = 99 };

        Assert.Equal(TrustDecision.InvalidSignature, TrustBindingVerifier.Verify(binding, _certificate, TrustState.Empty, out _));
    }

    [Fact]
    public void BindingForOtherCertificate_IsRejected()
    {
        var presented = TestAnchor.RandomCertificateHash();

        Assert.Equal(TrustDecision.CertificateMismatch, TrustBindingVerifier.Verify(_anchor.Sign(1, _certificate), presented, TrustState.Empty, out _));
    }

    [Fact]
    public void OlderSerial_IsRejected()
    {
        var pinned = Pin(serial: 5);
        var retired = TestAnchor.RandomCertificateHash();

        Assert.Equal(TrustDecision.OutdatedSerial, TrustBindingVerifier.Verify(_anchor.Sign(4, retired), retired, pinned, out _));
    }

    [Fact]
    public void SameSerialForDifferentCertificate_IsRejected()
    {
        var pinned = Pin(serial: 5);
        var other = TestAnchor.RandomCertificateHash();

        Assert.Equal(TrustDecision.OutdatedSerial, TrustBindingVerifier.Verify(_anchor.Sign(5, other), other, pinned, out _));
    }

    public static TheoryData<string, string, long, string> MalformedBindings() => new()
    {
        { "", "AAAA", 1, "AAAA" },
        { "not base64!", "AAAA", 1, "AAAA" },
        { "AAAA", "AAAA", 1, "AAAA" },
        { "AAAA", Convert.ToBase64String(new byte[32]), 0, "AAAA" },
        { "AAAA", Convert.ToBase64String(new byte[32]), -1, "AAAA" },
        { new string('A', 600), Convert.ToBase64String(new byte[32]), 1, "AAAA" },
        { "AAAA", Convert.ToBase64String(new byte[32]), 1, "" },
    };

    [Theory]
    [MemberData(nameof(MalformedBindings))]
    public void MalformedBinding_IsRejected(string anchor, string hash, long serial, string signature)
    {
        var binding = new TrustBinding(anchor, hash, serial, signature);

        Assert.Equal(TrustDecision.MalformedBinding, TrustBindingVerifier.Verify(binding, _certificate, TrustState.Empty, out _));
    }

    [Fact]
    public void MissingBinding_IsRejected()
    {
        Assert.Equal(TrustDecision.MalformedBinding, TrustBindingVerifier.Verify(null, _certificate, TrustState.Empty, out _));
    }

    [Fact]
    public void AnchorThatIsNotAnEcKey_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var binding = _anchor.Sign(1, _certificate) with { AnchorPublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) };

        Assert.Equal(TrustDecision.InvalidSignature, TrustBindingVerifier.Verify(binding, _certificate, TrustState.Empty, out _));
    }

    private TrustState Pin(long serial)
    {
        Assert.Equal(TrustDecision.Accepted, TrustBindingVerifier.Verify(_anchor.Sign(serial, _certificate), _certificate, TrustState.Empty, out var state));
        return state;
    }

    public void Dispose() => _anchor.Dispose();
}
