using System;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Endpoint metadata that attaches a <see cref="RequestTimeoutPolicy"/> to a route. Add an
/// instance to a route's metadata collection to override the global default policy for that
/// endpoint — including overriding it with <see cref="Disabled"/> to opt the endpoint out of
/// timeout enforcement entirely.
/// </summary>
/// <remarks>
/// <para>
/// The middleware resolves this metadata with last-wins semantics
/// (<c>IRouterRouteMetadataCollection.GetMetadata&lt;TMetadata&gt;</c>), so an endpoint-level
/// policy overrides a broader (for example group-level) one. The endpoint policy replaces the
/// global default outright — policies do not merge member-by-member.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IRequestTimeoutMetadata</c> interface. Metadata items in the endpoint bag are immutable
/// data carriers, and the sealed type guarantees the validated, immutable policy the middleware
/// reads at request time.
/// </para>
/// </remarks>
public sealed class RequestTimeoutMetadata
{
    /// <summary>
    /// Creates request-timeout metadata carrying the supplied policy.
    /// </summary>
    /// <param name="policy">The timeout policy applied to requests matching the route.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public RequestTimeoutMetadata(RequestTimeoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Policy = policy;
    }

    /// <summary>
    /// Creates request-timeout metadata for a plain timeout interval, answered with the default
    /// 504 status.
    /// </summary>
    /// <param name="timeout">The time a matching request may execute before it is timed out.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is zero or negative.</exception>
    public RequestTimeoutMetadata(TimeSpan timeout)
        : this(new RequestTimeoutPolicy { Timeout = timeout })
    {
    }

    /// <summary>
    /// Shared metadata that disables timeout enforcement for the endpoint it is attached to,
    /// overriding any global default (parity with ASP.NET's <c>DisableRequestTimeoutAttribute</c>).
    /// </summary>
    public static RequestTimeoutMetadata Disabled { get; } = new(RequestTimeoutPolicy.Disabled);

    /// <summary>
    /// Gets the timeout policy applied to requests matching the route. Never <see langword="null"/>.
    /// </summary>
    public RequestTimeoutPolicy Policy { get; }
}
