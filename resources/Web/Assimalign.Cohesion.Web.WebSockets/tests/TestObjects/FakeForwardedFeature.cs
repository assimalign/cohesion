using System.Net;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// The effective identity a trusted proxy chain would resolve: the scheme and host the client used
/// in front of the proxy, which <c>context.EffectiveScheme</c> and <c>EffectiveHost</c> read.
/// </summary>
internal sealed class FakeForwardedFeature : IHttpForwardedFeature
{
    public FakeForwardedFeature(HttpScheme scheme, string host, HttpScheme originalScheme, string originalHost)
    {
        Scheme = scheme;
        Host = new HttpHost(host);
        OriginalScheme = originalScheme;
        OriginalHost = new HttpHost(originalHost);
    }

    public string Name => "Assimalign.Cohesion.Http.Forwarded";

    public HttpScheme Scheme { get; }

    public HttpHost Host { get; }

    public EndPoint? RemoteEndPoint => null;

    public IPAddress? RemoteIp => null;

    public int RemotePort => 0;

    public HttpScheme OriginalScheme { get; }

    public HttpHost OriginalHost { get; }

    public EndPoint? OriginalRemoteEndPoint => null;

    public int TrustedHopCount => 1;
}
