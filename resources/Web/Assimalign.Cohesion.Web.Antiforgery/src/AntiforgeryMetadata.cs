using System;

using Assimalign.Cohesion.Web.Antiforgery.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery;

/// <summary>
/// Endpoint metadata that declares whether a route requires antiforgery validation. Attach
/// <see cref="Required"/> to make <c>UseAntiforgery</c> validate the route's unsafe-method requests, or
/// <see cref="Disabled"/> to exempt the route from a requirement declared more broadly (for example on
/// its route group).
/// </summary>
/// <remarks>
/// <para>
/// The middleware reads this metadata from the endpoint <c>UseRouting</c> publishes, last-wins
/// (<c>IRouterRouteMetadataCollection.GetMetadata&lt;AntiforgeryMetadata&gt;</c>), so the most specific
/// declaration decides: a route's <see cref="Disabled"/> overrides its group's <see cref="Required"/>,
/// and the reverse. The convention verbs <c>RequireAntiforgery()</c> and <c>DisableAntiforgery()</c>
/// append these instances. The Web endpoint-binding source generator appends <see cref="Required"/> to
/// every typed endpoint with a <c>[FromForm]</c> parameter or an uploaded-file parameter
/// (<c>IHttpFormFile</c> and its sequences), as route-level metadata, so a form-bound
/// endpoint opts out with its own <c>DisableAntiforgery()</c>; a group-level opt-out does not reach it.
/// </para>
/// <para>
/// Validation applies only when <c>UseAntiforgery</c> runs between <c>UseRouting</c> and the endpoint.
/// The metadata therefore implements <see cref="IRouteMiddlewareMetadata"/>: an endpoint that requires
/// validation fails with an <see cref="InvalidOperationException"/> when it is dispatched without
/// <c>UseAntiforgery</c> having processed it (the middleware is missing, or registered ahead of
/// <c>UseRouting</c>), instead of running unprotected. <see cref="Disabled"/> places no such requirement.
/// Routing applies the check last-wins per runtime type as well, which is why both states are instances
/// of this one sealed type: a route that disables antiforgery under a group that requires it requires
/// nothing.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract; there is deliberately no
/// <c>IAntiforgeryMetadata</c> interface. A second implementation would be a different runtime type, so
/// its <see cref="Disabled"/> could never supersede this type's <see cref="Required"/> in routing's
/// fail-closed check.
/// </para>
/// </remarks>
public sealed class AntiforgeryMetadata : IRouteMiddlewareMetadata
{
    private AntiforgeryMetadata(bool requiresValidation)
    {
        RequiresValidation = requiresValidation;
    }

    /// <summary>
    /// Gets the shared metadata that requires antiforgery validation for the endpoint it is attached to:
    /// <c>UseAntiforgery</c> rejects an unsafe-method request that does not carry a valid token pair.
    /// </summary>
    public static AntiforgeryMetadata Required { get; } = new(requiresValidation: true);

    /// <summary>
    /// Gets the shared metadata that exempts the endpoint it is attached to from antiforgery validation,
    /// overriding a requirement declared on an enclosing route group.
    /// </summary>
    public static AntiforgeryMetadata Disabled { get; } = new(requiresValidation: false);

    /// <summary>
    /// Gets whether the endpoint requires a valid antiforgery token pair on unsafe-method requests.
    /// </summary>
    public bool RequiresValidation { get; }

    /// <summary>
    /// Gets the pipeline verb that must process this metadata before the endpoint runs:
    /// <c>UseAntiforgery</c> for <see cref="Required"/>, or <see langword="null"/> for
    /// <see cref="Disabled"/>, which places no requirement.
    /// </summary>
    /// <remarks>
    /// Routing reads this when it dispatches the endpoint and fails the request with an
    /// <see cref="InvalidOperationException"/> when <c>UseAntiforgery</c> did not process it (see
    /// <see cref="IRouteMiddlewareMetadata"/>).
    /// </remarks>
    public string? RequiredMiddleware => RequiresValidation ? AntiforgeryMiddleware.Verb : null;
}
