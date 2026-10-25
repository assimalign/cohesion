using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An IS [NOT] NULL predicate.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlIsNullExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlIsNullExpression.DebugView))]
public sealed class GqlIsNullExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="operand">The value to test.</param>
    /// <param name="isNegated">Whether NOT is present.</param>
    /// <param name="location">The source span.</param>
    public GqlIsNullExpression(GqlExpression operand, bool isNegated, Location? location = null) : base(location)
    {
        Operand = operand;
        IsNegated = isNegated;
    }

    /// <summary>Gets the value to test.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets whether NOT is present.</summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlIsNullExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlIsNullExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Operand => _target.Operand;
        public bool IsNegated => _target.IsNegated;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
