using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route parameter value falls within an inclusive numeric range.
/// </summary>
internal sealed class RangeRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RangeRouteParameterPolicy" /> class.
    /// </summary>
    /// <param name="min">The minimum value.</param>
    /// <param name="max">The maximum value.</param>
    /// <remarks>The minimum value should be less than or equal to the maximum value.</remarks>
    internal RangeRouteParameterPolicy(long min, long max)
    {
        if (min > max)
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "The minimum value must be less than or equal to the maximum value.");
        }

        Min = min;
        Max = max;
    }

    /// <summary>
    /// Gets the minimum allowed value of the route parameter.
    /// </summary>
    public long Min { get; }

    /// <summary>
    /// Gets the maximum allowed value of the route parameter.
    /// </summary>
    public long Max { get; }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetParameterValue(out object? value) && value is not null)
        {
            string? valueString = Convert.ToString(value, CultureInfo.InvariantCulture);
            return CheckConstraintCore(valueString);
        }

        return false;
    }

    private bool CheckConstraintCore(string? valueString)
    {
        if (long.TryParse(valueString, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
        {
            return longValue >= Min && longValue <= Max;
        }

        return false;
    }
}
