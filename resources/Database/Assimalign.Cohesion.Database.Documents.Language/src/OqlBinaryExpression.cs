using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A binary arithmetic, comparison, or logical operation.</summary>
public sealed class OqlBinaryExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlBinaryExpression"/> class.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="op">The normalized operator: uppercase words and != for inequality.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="location">The source span.</param>
    public OqlBinaryExpression(OqlExpression left, string op, OqlExpression right, Location? location = null) : base(location)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    /// <summary>Gets the left operand.</summary>
    public OqlExpression Left { get; }
    /// <summary>Gets the normalized operator.</summary>
    public string Operator { get; }
    /// <summary>Gets the right operand.</summary>
    public OqlExpression Right { get; }
}
