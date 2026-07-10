using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Keeps a route candidate only when another route value matches an expected value.
/// </summary>
internal sealed class RequiredValueRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Creates a new route-value-based parameter policy.
    /// </summary>
    /// <param name="routeKey">The route value key that must exist.</param>
    /// <param name="expectedValue">The expected route value.</param>
    internal RequiredValueRouteParameterPolicy(string routeKey, string expectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeKey);
        ArgumentException.ThrowIfNullOrEmpty(expectedValue);

        RouteKey = routeKey;
        ExpectedValue = expectedValue;
    }

    /// <summary>
    /// Gets the route value key that must be present.
    /// </summary>
    public string RouteKey { get; }

    /// <summary>
    /// Gets the expected route value.
    /// </summary>
    public string ExpectedValue { get; }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.TryGetRouteValue(RouteKey, out object? routeValue) || routeValue is null)
        {
            return false;
        }

        string? currentValue = Convert.ToString(routeValue, CultureInfo.InvariantCulture);
        return string.Equals(currentValue, ExpectedValue, StringComparison.OrdinalIgnoreCase);
    }
}
