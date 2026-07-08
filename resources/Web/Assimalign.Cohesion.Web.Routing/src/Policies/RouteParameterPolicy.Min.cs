using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is an integer greater than or equal to an inclusive minimum
/// (<c>min(n)</c>).
/// </summary>
public sealed class MinRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MinRouteParameterPolicy"/> class.
    /// </summary>
    /// <param name="min">The inclusive minimum value.</param>
    internal MinRouteParameterPolicy(long min)
    {
        Min = min;
    }

    /// <summary>
    /// Gets the inclusive minimum allowed value of the route parameter.
    /// </summary>
    public long Min { get; }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetParameterValue(out object? value) && value is not null)
        {
            string? text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return parsed >= Min;
            }
        }

        return false;
    }
}
