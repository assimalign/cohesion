using System;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a boolean (<c>true</c> or <c>false</c>, case-insensitive) and
/// converts it to a <see cref="bool"/>.
/// </summary>
public sealed class BoolRouteParameterPolicy : TypedRouteParameterPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoolRouteParameterPolicy"/> class.
    /// </summary>
    internal BoolRouteParameterPolicy()
    {
    }

    /// <inheritdoc />
    public override Type ConversionType => typeof(bool);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (bool.TryParse(value, out bool result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
