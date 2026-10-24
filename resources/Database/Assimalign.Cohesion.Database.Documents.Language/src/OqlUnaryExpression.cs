using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A unary sign, logical NOT, or NULL, MISSING, or VALUED test.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlUnaryExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlUnaryExpression.DebugView))]
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlUnaryExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlUnaryExpression value)
        {
            _target = value;
        }

        public string Operator => _target.Operator;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Operand => _target.Operand;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
