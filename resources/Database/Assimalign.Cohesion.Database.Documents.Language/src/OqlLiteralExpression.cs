using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A null, Boolean, decimal number, or string literal.</summary>
/// <param name="value">The literal value.</param>
/// <param name="location">The source span.</param>
public sealed class OqlLiteralExpression(object? value, Location? location = null) : OqlExpression(location)
{
    /// <summary>Gets the literal's BCL value.</summary>
    public object? Value { get; } = value;
}
