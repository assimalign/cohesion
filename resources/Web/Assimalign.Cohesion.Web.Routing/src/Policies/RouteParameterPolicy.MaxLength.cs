using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value's text length is at most a given maximum (<c>maxlength(n)</c>).
/// </summary>
public sealed class MaxLengthRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MaxLengthRouteParameterPolicy"/> class.
    /// </summary>
    /// <param name="maxLength">The maximum number of characters the value may have.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    internal MaxLengthRouteParameterPolicy(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        MaxLength = maxLength;
    }

    /// <summary>
    /// Gets the maximum allowed length of the route value.
    /// </summary>
    public int MaxLength { get; }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetParameterValue(out object? value) && value is not null)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return text.Length <= MaxLength;
        }

        return false;
    }
}
