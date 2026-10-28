using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A list comprehension with a lexically scoped iterator and optional filter and projection.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlListComprehensionExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GqlListComprehensionExpression.DebugView))]
public sealed class GqlListComprehensionExpression : GqlExpression
{
    /// <summary>Initializes a list comprehension.</summary>
    /// <param name="variable">The iterator name, visible only in the predicate and projection.</param>
    /// <param name="list">The source list, evaluated in the enclosing scope.</param>
    /// <param name="predicate">The optional filter; only TRUE keeps an element.</param>
    /// <param name="projection">The optional projection; omission keeps the element.</param>
    /// <param name="location">The optional source span.</param>
    public GqlListComprehensionExpression(string variable, GqlExpression list, GqlExpression? predicate = null,
        GqlExpression? projection = null, Location? location = null) : base(location)
    {
        Variable = variable;
        List = list;
        Predicate = predicate;
        Projection = projection;
    }

    /// <summary>Gets the iterator name.</summary>
    public string Variable { get; }
    /// <summary>Gets the source list.</summary>
    public GqlExpression List { get; }
    /// <summary>Gets the optional filter.</summary>
    public GqlExpression? Predicate { get; }
    /// <summary>Gets the optional projection.</summary>
    public GqlExpression? Projection { get; }

    private sealed class DebugView
    {
        private readonly GqlListComprehensionExpression _target;

        public DebugView(GqlListComprehensionExpression value) { _target = value; }

        public string Variable => _target.Variable;
        public GqlExpression List => _target.List;
        public GqlExpression? Predicate => _target.Predicate;
        public GqlExpression? Projection => _target.Projection;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
