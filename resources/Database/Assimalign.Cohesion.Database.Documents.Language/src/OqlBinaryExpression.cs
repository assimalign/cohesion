using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A binary arithmetic, comparison, or logical operation.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlBinaryExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlBinaryExpression.DebugView))]
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlBinaryExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlBinaryExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Left => _target.Left;
        public string Operator => _target.Operator;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Right => _target.Right;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
