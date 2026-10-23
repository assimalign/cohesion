using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An ISO aggregate function, distinct from an ordinary scalar function call.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlAggregateExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlAggregateExpression.DebugView))]
public sealed class GqlAggregateExpression : GqlExpression
{
    /// <summary>Initializes an aggregate expression.</summary>
    /// <param name="function">The aggregate function.</param>
    /// <param name="operand">The value expression, or null for COUNT(*).</param>
    /// <param name="distinct">Whether duplicate operand values are eliminated.</param>
    /// <param name="location">The complete source span.</param>
    public GqlAggregateExpression(GqlAggregateFunction function, GqlExpression? operand = null,
        bool distinct = false, Location? location = null) : base(location)
    {
        Function = function;
        Operand = operand;
        Distinct = distinct;
    }

    /// <summary>Gets the aggregate function.</summary>
    public GqlAggregateFunction Function { get; }
    /// <summary>Gets the operand, or null for COUNT(*).</summary>
    public GqlExpression? Operand { get; }
    /// <summary>Gets whether duplicate non-null values are eliminated before aggregation.</summary>
    public bool Distinct { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlAggregateExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlAggregateExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlAggregateFunction Function => _target.Function;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression? Operand => _target.Operand;
        public bool Distinct => _target.Distinct;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
