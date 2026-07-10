using System;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates that a route value is a <see cref="Guid"/> and converts it to one.
/// </summary>
internal sealed class GuidRouteParameterPolicy : TypedRouteParameterPolicy
{
    /// <inheritdoc />
    public override Type ConversionType => typeof(Guid);

    /// <inheritdoc />
    public override bool TryConvert(string value, out object? converted)
    {
        if (Guid.TryParse(value, out Guid result))
        {
            converted = result;
            return true;
        }

        converted = null;
        return false;
    }
}
