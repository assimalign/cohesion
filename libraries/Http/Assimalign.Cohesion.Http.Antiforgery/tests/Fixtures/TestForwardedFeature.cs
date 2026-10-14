using System.Net;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

/// <summary>
/// An <see cref="IHttpForwardedFeature"/> double reporting the scheme a trusted proxy forwarded, the
/// way the forwarded-headers middleware publishes it, over a plaintext proxy-to-application hop.
/// </summary>
internal sealed class TestForwardedFeature : IHttpForwardedFeature
{
    public TestForwardedFeature(HttpScheme scheme)
    {
        Scheme = scheme;
    }

    public string Name => nameof(IHttpForwardedFeature);

    public HttpScheme Scheme { get; }

    public HttpHost Host => HttpHost.Empty;

    public EndPoint? RemoteEndPoint => null;

    public IPAddress? RemoteIp => null;

    public int RemotePort => 0;

    public HttpScheme OriginalScheme => HttpScheme.Http;

    public HttpHost OriginalHost => HttpHost.Empty;

    public EndPoint? OriginalRemoteEndPoint => null;

    public int TrustedHopCount => 1;
}
