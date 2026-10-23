using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A unary Boolean NOT or numeric sign expression.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlUnaryExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlUnaryExpression.DebugView))]
public sealed class GqlUnaryExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="operator">The operator: NOT, + or -.</param>
    /// <param name="operand">The operand.</param>
    /// <param name="location">The source span.</param>
    public GqlUnaryExpression(string @operator, GqlExpression operand, Location? location = null) : base(location)
    {
        Operator = @operator;
        Operand = operand;
    }

    /// <summary>Gets the operator: NOT, + or -.</summary>
    public string Operator { get; }
    /// <summary>Gets the operand.</summary>
    public GqlExpression Operand { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlUnaryExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlUnaryExpression value)
        {
            _target = value;
        }

        public string Operator => _target.Operator;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Operand => _target.Operand;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
