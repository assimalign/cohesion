using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a 64-bit signed integer and converts it to a <see cref="long"/>.
/// </summary>
internal sealed class LongRouteParameterPolicy : TypedRouteParameterPolicy
{
    /// <inheritdoc />
    public override Type ConversionType => typeof(long);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
