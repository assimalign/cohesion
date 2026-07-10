using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a date/time and converts it to a <see cref="DateTime"/>, parsed
/// with <see cref="CultureInfo.InvariantCulture"/> and <see cref="DateTimeStyles.None"/>.
/// </summary>
internal sealed class DateTimeRouteParameterPolicy : TypedRouteParameterPolicy
{
    /// <inheritdoc />
    public override Type ConversionType => typeof(DateTime);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
