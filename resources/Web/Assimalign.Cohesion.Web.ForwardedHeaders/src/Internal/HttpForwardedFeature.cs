using System.Net;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ForwardedHeaders.Internal;

/// <summary>
/// The <see cref="IHttpForwardedFeature"/> the forwarded-headers middleware attaches to
/// every exchange it processes. Immutable: one instance describes one exchange's
/// resolution outcome. When no hop was accepted the effective members equal the
/// original ones and <see cref="TrustedHopCount"/> is <c>0</c>.
/// </summary>
internal sealed class HttpForwardedFeature : IHttpForwardedFeature
{
    /// <summary>
    /// The stable feature slot name used when the feature is registered on the collection.
    /// </summary>
    public const string FeatureName = "Assimalign.Cohesion.Web.ForwardedHeaders";

    public HttpForwardedFeature(
        HttpScheme scheme,
        HttpHost host,
        EndPoint? remoteEndPoint,
        HttpScheme originalScheme,
        HttpHost originalHost,
        EndPoint? originalRemoteEndPoint,
        int trustedHopCount)
    {
        Scheme = scheme;
        Host = host;
        RemoteEndPoint = remoteEndPoint;
        OriginalScheme = originalScheme;
        OriginalHost = originalHost;
        OriginalRemoteEndPoint = originalRemoteEndPoint;
        TrustedHopCount = trustedHopCount;
    }

    public string Name => FeatureName;

    public HttpScheme Scheme { get; }

    public HttpHost Host { get; }

    public EndPoint? RemoteEndPoint { get; }

    public IPAddress? RemoteIp => (RemoteEndPoint as IPEndPoint)?.Address;

    public int RemotePort => (RemoteEndPoint as IPEndPoint)?.Port ?? 0;

    public HttpScheme OriginalScheme { get; }

    public HttpHost OriginalHost { get; }

    public EndPoint? OriginalRemoteEndPoint { get; }

    public int TrustedHopCount { get; }
}
