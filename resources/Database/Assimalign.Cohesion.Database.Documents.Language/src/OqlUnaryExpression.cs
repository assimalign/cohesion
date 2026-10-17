using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A unary sign, logical NOT, or NULL, MISSING, or VALUED test.</summary>
public sealed class OqlUnaryExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlUnaryExpression"/> class.</summary>
    /// <param name="op">The operator: +, -, NOT, IS NULL, IS NOT NULL, IS MISSING, IS NOT MISSING, IS VALUED, or IS NOT VALUED.</param>
    /// <param name="operand">The operand.</param>
    /// <param name="location">The source span.</param>
    public OqlUnaryExpression(string op, OqlExpression operand, Location? location = null) : base(location)
    {
        Operator = op;
        Operand = operand;
    }

    /// <summary>Gets the normalized operator.</summary>
    public string Operator { get; }
    /// <summary>Gets the operand.</summary>
    public OqlExpression Operand { get; }
}
