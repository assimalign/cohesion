using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a double-precision floating-point number and converts it to a
/// <see cref="double"/>.
/// </summary>
public sealed class DoubleRouteParameterPolicy : TypedRouteParameterPolicy
{
    private const NumberStyles Styles = NumberStyles.Float | NumberStyles.AllowThousands;

    /// <summary>
    /// Initializes a new instance of the <see cref="DoubleRouteParameterPolicy"/> class.
    /// </summary>
    internal DoubleRouteParameterPolicy()
    {
    }

    /// <inheritdoc />
    public override Type ConversionType => typeof(double);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (double.TryParse(value, Styles, CultureInfo.InvariantCulture, out double result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
