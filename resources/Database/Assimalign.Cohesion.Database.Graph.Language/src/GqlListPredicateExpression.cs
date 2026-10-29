using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A three-valued predicate over a lexically bound list iterator.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlListPredicateExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GqlListPredicateExpression.DebugView))]
public sealed class GqlListPredicateExpression : GqlExpression
{
    /// <summary>Initializes a list predicate.</summary>
    /// <param name="kind">The quantification rule.</param>
    /// <param name="variable">The iterator name, visible only in the predicate.</param>
    /// <param name="list">The source list, evaluated in the enclosing scope.</param>
    /// <param name="predicate">The Boolean or NULL test for each visited element.</param>
    /// <param name="location">The optional source span.</param>
    public GqlListPredicateExpression(GqlListPredicateKind kind, string variable, GqlExpression list,
        GqlExpression predicate, Location? location = null) : base(location)
    {
        Kind = kind;
        Variable = variable;
        List = list;
        Predicate = predicate;
    }

    /// <summary>Gets the quantification rule.</summary>
    public GqlListPredicateKind Kind { get; }
    /// <summary>Gets the iterator name.</summary>
    public string Variable { get; }
    /// <summary>Gets the source list.</summary>
    public GqlExpression List { get; }
    /// <summary>Gets the per-element test.</summary>
    public GqlExpression Predicate { get; }

    private sealed class DebugView
    {
        private readonly GqlListPredicateExpression _target;

        public DebugView(GqlListPredicateExpression value) { _target = value; }

        public GqlListPredicateKind Kind => _target.Kind;
        public string Variable => _target.Variable;
        public GqlExpression List => _target.List;
        public GqlExpression Predicate => _target.Predicate;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}

/// <summary>The supported list quantifiers.</summary>
public enum GqlListPredicateKind
{
    /// <summary>True when every element passes; true for an empty list.</summary>
    All,
    /// <summary>True when at least one element passes; false for an empty list.</summary>
    Any,
    /// <summary>True when no element passes; true for an empty list.</summary>
    None,
    /// <summary>True when exactly one element passes; false for an empty list.</summary>
    Single,
}
