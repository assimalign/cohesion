using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A binary arithmetic, comparison, or logical operation.</summary>
/// <param name="left">The left operand.</param>
/// <param name="op">The normalized operator: uppercase words and != for inequality.</param>
/// <param name="right">The right operand.</param>
/// <param name="location">The source span.</param>
public sealed class OqlBinaryExpression(OqlExpression left, string op, OqlExpression right, Location? location = null) : OqlExpression(location)
{
    /// <summary>Gets the left operand.</summary>
    public OqlExpression Left { get; } = left;
    /// <summary>Gets the normalized operator.</summary>
    public string Operator { get; } = op;
    /// <summary>Gets the right operand.</summary>
    public OqlExpression Right { get; } = right;
}
