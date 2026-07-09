using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a decimal number and converts it to a <see cref="decimal"/>.
/// </summary>
internal sealed class DecimalRouteParameterPolicy : TypedRouteParameterPolicy
{
    /// <inheritdoc />
    public override Type ConversionType => typeof(decimal);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
