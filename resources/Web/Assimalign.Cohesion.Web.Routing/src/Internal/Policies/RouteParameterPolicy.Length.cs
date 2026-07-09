using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value's text length is exactly a given length (<c>length(n)</c>) or falls
/// within an inclusive length range (<c>length(min,max)</c>).
/// </summary>
internal sealed class LengthRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LengthRouteParameterPolicy"/> class enforcing an
    /// exact length.
    /// </summary>
    /// <param name="length">The exact number of characters the value must have.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    internal LengthRouteParameterPolicy(int length)
        : this(length, length)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LengthRouteParameterPolicy"/> class enforcing an
    /// inclusive length range.
    /// </summary>
    /// <param name="minLength">The minimum number of characters.</param>
    /// <param name="maxLength">The maximum number of characters.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minLength"/> is negative, or <paramref name="maxLength"/> is less than <paramref name="minLength"/>.
    /// </exception>
    internal LengthRouteParameterPolicy(int minLength, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minLength);
        if (maxLength < minLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "The maximum length must be greater than or equal to the minimum length.");
        }

        MinLength = minLength;
        MaxLength = maxLength;
    }

    /// <summary>
    /// Gets the minimum allowed length of the route value.
    /// </summary>
    public int MinLength { get; }

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
            return text.Length >= MinLength && text.Length <= MaxLength;
        }

        return false;
    }
}
