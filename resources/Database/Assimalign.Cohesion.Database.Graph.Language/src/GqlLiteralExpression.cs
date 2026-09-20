using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A null, Boolean, signed 64-bit integer, floating-point, or string literal.</summary>
/// <param name="value">The boxed scalar value.</param>
/// <param name="location">The source span.</param>
public sealed class GqlLiteralExpression(object? value, Location? location = null) : GqlExpression(location)
{
    /// <summary>Gets the scalar value.</summary>
    public object? Value { get; } = value;
}
