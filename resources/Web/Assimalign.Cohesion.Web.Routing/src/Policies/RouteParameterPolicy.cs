namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Base class for an executable route parameter policy (an inline constraint such as
/// <c>int</c>, <c>range(1,10)</c>, or <c>regex(...)</c>). A policy is evaluated while a route
/// is matched; when it does not apply the candidate route is rejected for that request.
/// </summary>
/// <remarks>
/// <para>
/// This is the public extension point for custom constraints: derive from this type (or from
/// <see cref="TypedRouteParameterPolicy"/> to also contribute a typed conversion) and register the
/// derived policy through a <see cref="RouteParameterPolicyMap"/>.
/// </para>
/// <para>
/// Policies that only validate the raw text (for example <c>regex</c>, <c>length</c>, <c>min</c>)
/// leave the route value a <see cref="string"/>. Policies deriving from
/// <see cref="TypedRouteParameterPolicy"/> replace the raw value with a strongly-typed one so
/// downstream consumers read it without re-parsing.
/// </para>
/// </remarks>
public abstract class RouteParameterPolicy
{
    /// <summary>
    /// Determines whether this policy applies to (accepts) the parameter value in the supplied context.
    /// </summary>
    /// <param name="context">The current route parameter policy context, carrying the captured route values.</param>
    /// <returns><see langword="true"/> when the value satisfies the policy; otherwise <see langword="false"/>.</returns>
    public abstract bool Applies(RouteParameterPolicyContext context);
}
