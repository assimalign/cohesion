using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a 32-bit signed integer and converts it to an <see cref="int"/>.
/// </summary>
public sealed class IntRouteParameterPolicy : TypedRouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IntRouteParameterPolicy"/> class.
    /// </summary>
    internal IntRouteParameterPolicy()
    {
    }

    /// <inheritdoc />
    public override Type ConversionType => typeof(int);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
