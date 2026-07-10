using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is an integer less than or equal to an inclusive maximum
/// (<c>max(n)</c>).
/// </summary>
internal sealed class MaxRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MaxRouteParameterPolicy"/> class.
    /// </summary>
    /// <param name="max">The inclusive maximum value.</param>
    internal MaxRouteParameterPolicy(long max)
    {
        Max = max;
    }

    /// <summary>
    /// Gets the inclusive maximum allowed value of the route parameter.
    /// </summary>
    public long Max { get; }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetParameterValue(out object? value) && value is not null)
        {
            string? text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return parsed <= Max;
            }
        }

        return false;
    }
}
