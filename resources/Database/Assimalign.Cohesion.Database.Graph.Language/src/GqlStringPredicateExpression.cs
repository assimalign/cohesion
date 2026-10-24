using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An ordinal, case-sensitive string predicate.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlStringPredicateExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlStringPredicateExpression.DebugView))]
public sealed class GqlStringPredicateExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="left">The string to search.</param>
    /// <param name="operator">The predicate operator.</param>
    /// <param name="right">The search string.</param>
    /// <param name="location">The source span.</param>
    public GqlStringPredicateExpression(GqlExpression left, GqlStringPredicateOperator @operator, GqlExpression right, Location? location = null) : base(location)
    {
        Left = left;
        Operator = @operator;
        Right = right;
    }

    /// <summary>Gets the string to search.</summary>
    public GqlExpression Left { get; }
    /// <summary>Gets the predicate operator.</summary>
    public GqlStringPredicateOperator Operator { get; }
    /// <summary>Gets the search string.</summary>
    public GqlExpression Right { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlStringPredicateExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlStringPredicateExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Left => _target.Left;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlStringPredicateOperator Operator => _target.Operator;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression Right => _target.Right;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
