using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Authorization.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// Endpoint metadata that requires authorization for a route, or that allows anonymous access to it
/// (<see cref="AllowAnonymous"/>). The convention verbs <c>RequireAuthorization</c> and
/// <c>AllowAnonymous</c> attach it to routes and groups; adding an instance to a route's metadata
/// collection is equivalent.
/// </summary>
/// <remarks>
/// <para>
/// One item declares a requirement from up to four parts: a named policy (<see cref="PolicyName"/>,
/// resolved against <see cref="AuthorizationOptions.AddPolicy(string, AuthorizationPolicy)"/>), an
/// inline <see cref="Policy"/>, <see cref="Roles"/>, and <see cref="AuthenticationSchemes"/>. An item
/// that names no policy and no roles requires <see cref="AuthorizationOptions.DefaultPolicy"/>.
/// </para>
/// <para>
/// <b>Combination.</b> <c>UseAuthorization</c> reads every item on the endpoint, outer group first
/// (<c>GetOrderedMetadata</c>), not only the last one: a group's requirement and a route's requirement
/// both apply, and the request must satisfy all of them. Their authentication schemes are combined
/// (a union, in order). The most specific <see cref="AllowAnonymous"/> wins over everything declared
/// before it: it clears the requirements of the groups above it and of earlier items on its own builder,
/// while a requirement declared after it still applies. A route's <see cref="AllowAnonymous"/> therefore
/// opens that route inside a protected group, and a route's requirement inside an anonymous group stays
/// in force. When the last item is <see cref="AllowAnonymous"/>, the endpoint is not authorized at all.
/// <see cref="AuthorizationOptions.GetEffectivePolicy"/> performs this combination for the middleware and
/// for any component that describes endpoints.
/// </para>
/// <para>
/// <b>Fail closed.</b> Authorization applies only when <c>UseAuthorization</c> runs between
/// <c>UseRouting</c> and the endpoint, so this type implements <see cref="IRouteMiddlewareMetadata"/>:
/// an endpoint whose authorization metadata requires authorization fails with an
/// <see cref="InvalidOperationException"/> when it is dispatched without <c>UseAuthorization</c>
/// having processed it (the middleware is missing, or registered ahead of <c>UseRouting</c>), instead
/// of running unauthorized. Routing checks only the last item of each runtime type, so the
/// allow-anonymous marker is an instance of this type rather than a type of its own, and the last item
/// decides both questions the same way: when a route's <see cref="AllowAnonymous"/> follows its group's
/// requirement, the route is anonymous and runs with or without <c>UseAuthorization</c>; in the reverse
/// order (an outer <see cref="AllowAnonymous"/>, then a route requirement) the route is protected and
/// fails closed until the middleware is registered.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract; there is deliberately no
/// <c>IAuthorizationMetadata</c> interface. Endpoint metadata items are immutable data carriers, and
/// the sealed type guarantees the validated values the middleware reads.
/// </para>
/// </remarks>
public sealed class AuthorizationMetadata : IRouteMiddlewareMetadata
{
    private readonly IReadOnlyList<string> _roles = Array.Empty<string>();
    private readonly IReadOnlyList<string> _authenticationSchemes = Array.Empty<string>();

    private AuthorizationMetadata(string? policyName, AuthorizationPolicy? policy, bool allowsAnonymous)
    {
        PolicyName = policyName;
        Policy = policy;
        AllowsAnonymous = allowsAnonymous;
    }

    /// <summary>
    /// Creates metadata that requires <see cref="AuthorizationOptions.DefaultPolicy"/>, unless
    /// <see cref="Roles"/> are set.
    /// </summary>
    public AuthorizationMetadata()
        : this(policyName: null, policy: null, allowsAnonymous: false)
    {
    }

    /// <summary>
    /// Creates metadata that requires a named policy, resolved when the endpoint is first authorized
    /// against the policies registered with <see cref="AuthorizationOptions.AddPolicy(string, AuthorizationPolicy)"/>.
    /// </summary>
    /// <param name="policyName">The registered policy name, compared ordinal (case-sensitive).</param>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
    public AuthorizationMetadata(string policyName)
        : this(ValidatePolicyName(policyName), policy: null, allowsAnonymous: false)
    {
    }

    /// <summary>
    /// Creates metadata that requires an inline policy, without registering it by name.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    public AuthorizationMetadata(AuthorizationPolicy policy)
        : this(policyName: null, ValidatePolicy(policy), allowsAnonymous: false)
    {
    }

    /// <summary>
    /// Gets the shared metadata that allows anonymous access to the endpoint it is attached to. It clears
    /// every authorization requirement declared before it (a group's, or an earlier one on the same
    /// builder) and keeps the fallback policy from applying, but a requirement declared after it, by a
    /// nested group or the route, still applies. It places no requirement on the pipeline.
    /// </summary>
    public static AuthorizationMetadata AllowAnonymous { get; } = new(policyName: null, policy: null, allowsAnonymous: true);

    /// <summary>
    /// Gets the registered policy name the item requires, or <see langword="null"/>.
    /// </summary>
    public string? PolicyName { get; }

    /// <summary>
    /// Gets the inline policy the item requires, or <see langword="null"/>.
    /// </summary>
    public AuthorizationPolicy? Policy { get; }

    /// <summary>
    /// Gets or initializes the roles the item requires: the principal must be in at least one. Empty by
    /// default.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/>, empty, or whitespace entry.</exception>
    public IReadOnlyList<string> Roles
    {
        get => _roles;
        init => _roles = Array.AsReadOnly(AuthorizationNames.Copy(value, nameof(value), "role"));
    }

    /// <summary>
    /// Gets or initializes the authentication schemes that establish the principal the item is evaluated
    /// against. Empty by default, which evaluates <c>context.User</c>. Combined with the schemes of every
    /// other authorization item on the endpoint.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a <see langword="null"/>, empty, or whitespace entry.</exception>
    public IReadOnlyList<string> AuthenticationSchemes
    {
        get => _authenticationSchemes;
        init => _authenticationSchemes = Array.AsReadOnly(AuthorizationNames.Copy(value, nameof(value), "authentication scheme"));
    }

    /// <summary>
    /// Gets whether the item allows anonymous access (it is <see cref="AllowAnonymous"/>).
    /// </summary>
    public bool AllowsAnonymous { get; }

    /// <summary>
    /// Gets the pipeline verb that must process this metadata before the endpoint runs:
    /// <c>UseAuthorization</c> for a requirement, or <see langword="null"/> for
    /// <see cref="AllowAnonymous"/>, which places no requirement.
    /// </summary>
    /// <remarks>
    /// Routing reads this when it dispatches the endpoint and fails the request with an
    /// <see cref="InvalidOperationException"/> when <c>UseAuthorization</c> did not process it (see
    /// <see cref="IRouteMiddlewareMetadata"/>).
    /// </remarks>
    public string? RequiredMiddleware => AllowsAnonymous ? null : AuthorizationMiddleware.Verb;

    private static string ValidatePolicyName(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        return policyName;
    }

    private static AuthorizationPolicy ValidatePolicy(AuthorizationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy;
    }
}
