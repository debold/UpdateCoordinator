using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace SUSyphos.TestSupport;

/// <summary>
/// Runs the central server on a real Kestrel HTTPS endpoint (random local port), so tests cover the TLS handshake.
/// </summary>
public sealed class KestrelServerFactory : ServerFactory
{
    public KestrelServerFactory()
    {
        UseKestrel();
    }

    /// <summary>HTTPS address of the running server.</summary>
    public Uri BaseAddress
    {
        get
        {
            StartServer();
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            return new Uri(addresses.Single(a => a.StartsWith("https://", StringComparison.Ordinal)) + "/");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Kestrel:Endpoints:Https:Url", "https://127.0.0.1:0");
    }
}
