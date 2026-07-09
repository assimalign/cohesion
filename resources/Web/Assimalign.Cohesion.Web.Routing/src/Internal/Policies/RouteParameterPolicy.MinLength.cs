using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value's text length is at least a given minimum (<c>minlength(n)</c>).
/// </summary>
internal sealed class MinLengthRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MinLengthRouteParameterPolicy"/> class.
    /// </summary>
    /// <param name="minLength">The minimum number of characters the value must have.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minLength"/> is negative.</exception>
    internal MinLengthRouteParameterPolicy(int minLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minLength);
        MinLength = minLength;
    }

    /// <summary>
    /// Gets the minimum allowed length of the route value.
    /// </summary>
    public int MinLength { get; }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetParameterValue(out object? value) && value is not null)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return text.Length >= MinLength;
        }

        return false;
    }
}
