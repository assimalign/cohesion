using System;

using Assimalign.Cohesion.Web.RequestTimeouts.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Endpoint metadata that attaches a <see cref="RequestTimeoutPolicy"/> to a route. Add an
/// instance to a route's metadata collection to override the global default policy for that
/// endpoint — including overriding it with <see cref="Disabled"/> to opt the endpoint out of
/// timeout enforcement entirely.
/// </summary>
/// <remarks>
/// <para>
/// The middleware reads this metadata from the endpoint <c>UseRouting</c> publishes, with
/// last-wins semantics (<c>IRouterRouteMetadataCollection.GetMetadata&lt;TMetadata&gt;</c>), so an
/// endpoint-level policy overrides a broader (for example group-level) one. The endpoint policy
/// replaces the global default outright — policies do not merge member-by-member.
/// </para>
/// <para>
/// A timeout is enforced only when <c>UseRequestTimeouts</c> runs between <c>UseRouting</c> and the
/// endpoint. The metadata therefore implements <see cref="IRouteMiddlewareMetadata"/>: an endpoint
/// whose policy carries a timeout fails with an <see cref="InvalidOperationException"/> when it is
/// dispatched without <c>UseRequestTimeouts</c> having processed it (the middleware is missing, or
/// registered ahead of <c>UseRouting</c>), instead of running unbounded. A policy that disables the
/// timeout, <see cref="Disabled"/> included, places no such requirement.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IRequestTimeoutMetadata</c> interface. Metadata items in the endpoint bag are immutable
/// data carriers, and the sealed type guarantees the validated, immutable policy the middleware
/// reads at request time.
/// </para>
/// </remarks>
public sealed class RequestTimeoutMetadata : IRouteMiddlewareMetadata
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

    /// <summary>
    /// Gets the pipeline verb that must process this metadata before the endpoint runs:
    /// <c>UseRequestTimeouts</c> when <see cref="Policy"/> carries a timeout, or
    /// <see langword="null"/> when the policy disables the timeout (<see cref="Disabled"/>, or any
    /// policy with a <see langword="null"/> <see cref="RequestTimeoutPolicy.Timeout"/>), which places
    /// no requirement.
    /// </summary>
    /// <remarks>
    /// Routing reads this when it dispatches the endpoint and fails the request with an
    /// <see cref="InvalidOperationException"/> when <c>UseRequestTimeouts</c> did not process it (see
    /// <see cref="IRouteMiddlewareMetadata"/>).
    /// </remarks>
    public string? RequiredMiddleware => Policy.Timeout is null ? null : RequestTimeoutMiddleware.Verb;
}
