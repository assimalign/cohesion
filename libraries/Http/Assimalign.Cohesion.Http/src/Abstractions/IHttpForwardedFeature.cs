using System.Net;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The effective connection identity for the current exchange after a trusted
/// proxy chain has been evaluated &#8212; the client the request is really
/// <em>from</em>, as opposed to the peer the transport is connected to.
/// </summary>
/// <remarks>
/// <para>
/// Behind a reverse proxy, load balancer, or gateway, the transport-level
/// <see cref="IHttpContext.ConnectionInfo"/> describes the nearest hop and
/// <see cref="IHttpRequest.Scheme"/>/<see cref="IHttpRequest.Host"/> describe the
/// proxy-to-server leg. The original client identity travels in the RFC 7239
/// <c>Forwarded</c> header or the de-facto <c>X-Forwarded-For</c>/<c>-Proto</c>/<c>-Host</c>
/// headers &#8212; attacker-writable input that must pass a trust model before it is
/// believed. This feature is the <em>output</em> of that evaluation: a producer (the
/// forwarded-headers middleware in <c>Assimalign.Cohesion.Web</c>) walks the forwarded
/// chain rightmost-first through the hops it trusts and attaches the result to
/// <see cref="IHttpContext.Features"/>, one instance per exchange.
/// </para>
/// <para>
/// The wire-level surfaces are deliberately get-only and are <em>never</em> mutated:
/// <see cref="IHttpRequest.Scheme"/>, <see cref="IHttpRequest.Host"/>, the raw request
/// headers, and <see cref="IHttpContext.ConnectionInfo"/> keep reporting exactly what the
/// transport saw. Downstream consumers that want the client-facing values read them
/// through this feature &#8212; most conveniently via the
/// <see cref="HttpContextForwardedExtensions"/> members
/// (<c>context.EffectiveScheme</c>, <c>context.EffectiveHost</c>,
/// <c>context.EffectiveRemoteEndPoint</c>, <c>context.EffectiveRemoteIp</c>), which fall
/// back to the wire values when no feature is attached. That feature-first read
/// convention is the repo-wide answer to "how do I get the client IP/scheme/host":
/// consult the feature (or the <c>Effective*</c> members), never re-parse forwarding
/// headers locally.
/// </para>
/// <para>
/// When the producer accepted no forwarded hop (no forwarding headers, an untrusted
/// direct peer, or an unusable header), the effective members equal the original ones
/// and <see cref="TrustedHopCount"/> is <c>0</c> &#8212; the feature always answers the
/// "effective identity" question, even when the answer is "the wire values".
/// </para>
/// </remarks>
public interface IHttpForwardedFeature : IHttpFeature
{
    /// <summary>
    /// Gets the effective request scheme &#8212; the scheme the client used on the
    /// outermost trusted hop (e.g. <see cref="HttpScheme.Https"/> when TLS terminates
    /// at the proxy), or the wire scheme when no <c>proto</c> value was resolved.
    /// </summary>
    HttpScheme Scheme { get; }

    /// <summary>
    /// Gets the effective host &#8212; the authority the client addressed on the
    /// outermost trusted hop, or the wire host when no <c>host</c> value was resolved.
    /// </summary>
    HttpHost Host { get; }

    /// <summary>
    /// Gets the effective remote endpoint &#8212; the address of the outermost node the
    /// trusted chain vouches for, or the transport's
    /// <see cref="IHttpConnectionInfo.RemoteEndPoint"/> when no <c>for</c> value was
    /// resolved. The port is <c>0</c> when the forwarded node carried none.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the effective remote IP address, or <see langword="null"/> when
    /// <see cref="RemoteEndPoint"/> is not an IP endpoint.
    /// </summary>
    IPAddress? RemoteIp { get; }

    /// <summary>
    /// Gets the effective remote port, or <c>0</c> when the effective endpoint carries
    /// no port (mirroring <see cref="IHttpConnectionInfo.RemotePort"/> semantics).
    /// </summary>
    int RemotePort { get; }

    /// <summary>
    /// Gets the scheme the transport observed for this exchange, before any forwarded
    /// values were applied.
    /// </summary>
    HttpScheme OriginalScheme { get; }

    /// <summary>
    /// Gets the host the transport observed for this exchange, before any forwarded
    /// values were applied.
    /// </summary>
    HttpHost OriginalHost { get; }

    /// <summary>
    /// Gets the transport-level remote endpoint (the directly connected peer), before
    /// any forwarded values were applied.
    /// </summary>
    EndPoint? OriginalRemoteEndPoint { get; }

    /// <summary>
    /// Gets the number of forwarded hops the trust evaluation accepted. <c>0</c> means
    /// nothing was resolved and the effective members equal the original ones.
    /// </summary>
    int TrustedHopCount { get; }
}
