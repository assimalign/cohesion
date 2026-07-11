using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The explicit trust model for the forwarded-headers middleware: which forwarding
/// headers to honor, which peers are believed when they claim to forward for someone
/// else, and how deep into the forwarded chain resolution may walk.
/// </summary>
/// <remarks>
/// <para>
/// Forwarding headers are client-writable input. The middleware therefore believes an
/// entry only when the hop that <em>handed it over</em> is inside the trust boundary
/// defined here: the directly connected peer must be trusted for the rightmost entry to
/// apply, that entry's address must be trusted for the next entry to apply, and so on,
/// rightmost-first, at most <see cref="ForwardLimit"/> hops. Everything to the left of
/// the first untrusted or unusable hop is ignored.
/// </para>
/// <para>
/// The options object is a builder-time surface: <c>UseForwardedHeaders</c> validates and
/// snapshots it when the pipeline is composed, so later mutation has no effect on a
/// running application.
/// </para>
/// </remarks>
public sealed class ForwardedHeadersOptions
{
    /// <summary>
    /// Gets or sets the forwarding headers to honor. Defaults to
    /// <see cref="ForwardedHeaders.None"/>; an explicit selection is required &#8212; enable
    /// exactly the headers your trusted proxy manages (see <see cref="ForwardedHeaders"/>
    /// for why honoring an unmanaged header is spoofable).
    /// </summary>
    public ForwardedHeaders Headers { get; set; } = ForwardedHeaders.None;

    /// <summary>
    /// Gets or sets the maximum number of forwarded hops to accept, or
    /// <see langword="null"/> for no limit. Defaults to <c>1</c> &#8212; exactly the entry
    /// appended by the directly connected proxy. Raise it only to the number of trusted
    /// proxies actually chained in front of the service.
    /// </summary>
    public int? ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Gets the addresses of individual trusted proxies. Defaults to the IPv4 and IPv6
    /// loopback addresses (a reverse proxy on the same host); call <c>Clear()</c> to
    /// harden a deployment where the proxy is remote. IPv4-mapped IPv6 addresses are
    /// normalized to IPv4 before comparison.
    /// </summary>
    public IList<IPAddress> KnownProxies { get; } = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };

    /// <summary>
    /// Gets the CIDR ranges of trusted proxies (e.g. <c>IPNetwork.Parse("10.0.0.0/8")</c>
    /// for an in-cluster gateway fleet). Defaults to empty. Addresses are normalized
    /// (IPv4-mapped IPv6 &#8594; IPv4) before the containment check, so declare networks in
    /// their native family.
    /// </summary>
    public IList<IPNetwork> KnownNetworks { get; } = new List<IPNetwork>();

    /// <summary>
    /// Gets or sets whether a directly connected peer on a non-IP transport &#8212; a Unix
    /// domain socket, named pipe, or in-memory endpoint &#8212; is trusted as the first hop.
    /// Defaults to <see langword="true"/>: those transports are machine-local by
    /// construction, and a local reverse proxy forwarding over IPC is the canonical
    /// topology they exist for (the same rationale as the loopback default in
    /// <see cref="KnownProxies"/>). Set to <see langword="false"/> to ignore forwarding
    /// headers arriving over such transports. Subsequent hops always carry IP addresses
    /// and are checked against <see cref="KnownProxies"/>/<see cref="KnownNetworks"/>.
    /// </summary>
    public bool TrustLocalTransports { get; set; } = true;
}
