using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A null, Boolean, signed 64-bit integer, floating-point, or string literal.</summary>
public sealed class GqlLiteralExpression : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlLiteralExpression"/> class.</summary>
    /// <param name="value">The boxed scalar value.</param>
    /// <param name="location">The source span.</param>
    public GqlLiteralExpression(object? value, Location? location = null) : base(location)
    {
        Value = value;
    }

    /// <summary>Gets the scalar value.</summary>
    public object? Value { get; }
}
