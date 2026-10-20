using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts;
using SUSyphos.Contracts.Trust;
using SUSyphos.Server.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class TrustBindingEndpointTests
{
    private static readonly Uri BindingUri = new(ApiRoutes.TrustBinding, UriKind.Relative);

    [Fact]
    public async Task Binding_IsSignedByAnchorForCurrentCertificate()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var binding = await client.GetFromJsonAsync<TrustBinding>(BindingUri, TestContext.Current.CancellationToken);

        Assert.NotNull(binding);
        Assert.Equal(1, binding.Serial);
        var certificateHash = TrustBindingFormat.ComputeSpkiHash(factory.Certificate);
        Assert.Equal(Convert.ToBase64String(certificateHash), binding.CertificateSpkiSha256);

        using var anchor = ECDsa.Create();
        anchor.ImportSubjectPublicKeyInfo(Convert.FromBase64String(binding.AnchorPublicKey), out _);
        Assert.True(anchor.VerifyData(
            TrustBindingFormat.GetSignedBytes(binding.Serial, certificateHash),
            Convert.FromBase64String(binding.Signature),
            TrustBindingFormat.HashAlgorithm));
    }

    [Fact]
    public async Task Binding_WithUnchangedCertificate_KeepsSerial()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        await factory.ReplaceCertificateAsync(factory.Certificate);
        var binding = await client.GetFromJsonAsync<TrustBinding>(BindingUri, TestContext.Current.CancellationToken);

        Assert.Equal(1, binding?.Serial);
    }

    [Fact]
    public async Task Binding_AfterCertificateChange_HasHigherSerialAndSameAnchor()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<TrustBinding>(BindingUri, TestContext.Current.CancellationToken);

        using var renewed = TestCertificates.Create();
        await factory.ReplaceCertificateAsync(renewed);
        var after = await client.GetFromJsonAsync<TrustBinding>(BindingUri, TestContext.Current.CancellationToken);

        Assert.Equal(2, after?.Serial);
        Assert.Equal(before?.AnchorPublicKey, after?.AnchorPublicKey);
        Assert.Equal(Convert.ToBase64String(TrustBindingFormat.ComputeSpkiHash(renewed)), after?.CertificateSpkiSha256);
    }

    [Fact]
    public async Task Binding_IsAvailableWithoutCredentials()
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(BindingUri, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Binding_CannotBeModified(string method)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), BindingUri);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}

public sealed class TrustAnchorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));

    private string AnchorPath => Path.Combine(_directory, "anchor.key");

    [Fact]
    public void Anchor_IsCreatedOnceAndReloaded()
    {
        byte[] first;
        using (var anchor = Create())
        {
            first = anchor.PublicKey;
        }

        using var reloaded = Create();

        Assert.Equal(first, reloaded.PublicKey);
    }

    [Fact]
    public void Anchor_IsNotStoredInPlainText()
    {
        using var anchor = Create();
        var publicPoint = anchor.PublicKey[^65..];

        // A plain PKCS#8 export would contain the public key point; the DPAPI ciphertext does not.
        var stored = File.ReadAllBytes(AnchorPath);

        Assert.True(stored.AsSpan().IndexOf(publicPoint) < 0);
    }

    [Fact]
    public void CorruptAnchor_IsNeverReplacedSilently()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(AnchorPath, [1, 2, 3]);
        using var anchor = Create();

        Assert.Throws<InvalidOperationException>(() => anchor.PublicKey);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(AnchorPath));
    }

    [Fact]
    public void Signature_FromAnchor_Verifies()
    {
        using var anchor = Create();
        var data = "payload"u8.ToArray();

        var signature = anchor.Sign(data);

        Assert.True(anchor.Verify(data, signature));
        Assert.False(anchor.Verify("other"u8.ToArray(), signature));
    }

    private TrustAnchor Create() => new(Options.Create(new TrustOptions { AnchorPath = AnchorPath }), NullLogger<TrustAnchor>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
