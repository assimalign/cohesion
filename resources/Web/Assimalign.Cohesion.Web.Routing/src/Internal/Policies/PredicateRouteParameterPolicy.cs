using System;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Evaluates a route parameter using a custom predicate.
/// </summary>
internal sealed class PredicateRouteParameterPolicy : RouteParameterPolicy
{
    private readonly Func<RouteParameterPolicyContext, bool> _predicate;

    /// <summary>
    /// Creates a new predicate-based route parameter policy.
    /// </summary>
    /// <param name="predicate">The predicate used to evaluate the current route parameter context.</param>
    internal PredicateRouteParameterPolicy(Func<RouteParameterPolicyContext, bool> predicate)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _predicate(context);
    }
}
