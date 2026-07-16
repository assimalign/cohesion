using System.Net;

namespace Assimalign.Cohesion.Http.Forwarded.Tests.TestObjects;

/// <summary>
/// Settable <see cref="IHttpForwardedFeature"/> stub standing in for a producer's
/// resolution outcome.
/// </summary>
internal sealed class StubForwardedFeature : IHttpForwardedFeature
{
    public string Name => "Assimalign.Cohesion.Http.Forwarded.Tests.Stub";

    public HttpScheme Scheme { get; init; }

    public HttpHost Host { get; init; }

    public EndPoint? RemoteEndPoint { get; init; }

    public IPAddress? RemoteIp => (RemoteEndPoint as IPEndPoint)?.Address;

    public int RemotePort => (RemoteEndPoint as IPEndPoint)?.Port ?? 0;

    public HttpScheme OriginalScheme { get; init; }

    public HttpHost OriginalHost { get; init; }

    public EndPoint? OriginalRemoteEndPoint { get; init; }

    public int TrustedHopCount { get; init; }
}
