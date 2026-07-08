using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configures whether an <see cref="HttpConnectionListener"/> advertises its HTTP/3 (QUIC) listener
/// to HTTP/1.1 and HTTP/2 clients via an RFC 7838 <c>Alt-Svc</c> response header, so a client that
/// started on a TCP-based protocol can discover and upgrade to the h3 endpoint (RFC 9114 §3.1).
/// </summary>
/// <remarks>
/// <para>
/// Advertisement only takes effect when it is <see cref="Enabled"/> <em>and</em> the listener is
/// configured with both a stream protocol (HTTP/1.1 or HTTP/2) and an HTTP/3 multiplexed listener —
/// there is otherwise nothing to advertise or no TCP response to carry the header. When active, the
/// server injects <c>Alt-Svc: h3="&lt;authority&gt;"; ma=&lt;seconds&gt;</c> on HTTP/1.1 and HTTP/2
/// responses, deriving the port from the registered HTTP/3 listener endpoint unless
/// <see cref="Authority"/> supplies one explicitly.
/// </para>
/// <para>
/// The server never overwrites an <c>Alt-Svc</c> header the application set itself: the injection is
/// applied at response-head commit time only when the header is absent.
/// </para>
/// </remarks>
public sealed class HttpAltServiceAdvertisementOptions
{
    private TimeSpan _maxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets a value indicating whether HTTP/3 advertisement is enabled. Defaults to
    /// <see langword="false"/> — advertisement is opt-in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the freshness lifetime emitted as the <c>ma</c> parameter (RFC 7838 §3.1).
    /// Defaults to 24 hours. Serialized as whole seconds (fractional seconds are truncated).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is negative.</exception>
    public TimeSpan MaxAge
    {
        get => _maxAge;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The max-age must be non-negative.");
            }

            _maxAge = value;
        }
    }

    /// <summary>
    /// Gets or sets an explicit alt-authority for the advertised HTTP/3 endpoint, of the form
    /// <c>host:port</c> or <c>:port</c> (an empty host advertises the alternative on the request's
    /// own host). When <see langword="null"/> (the default), the authority is derived as
    /// <c>:&lt;port&gt;</c> from the registered HTTP/3 listener endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set this when the QUIC listener's bound port is not the port clients should reach it on — for
    /// example when the endpoint is behind a port-mapping load balancer — or when the alternative
    /// lives on a different host.
    /// </para>
    /// <para>
    /// Parameters beyond <c>ma</c> (such as <c>persist=1</c>) are deliberately not configurable
    /// here: an application that needs them sets the <c>Alt-Svc</c> header itself (for example via
    /// <see cref="HttpAltService"/>), and the server's injection always yields to an
    /// application-set value.
    /// </para>
    /// </remarks>
    public string? Authority { get; set; }
}
