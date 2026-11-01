using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A scalar document-key JOIN or NEST binding.</summary>
[System.Diagnostics.DebuggerDisplay("OqlJoinExpression {Collection,nq} AS {Variable,nq}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(OqlJoinExpression.DebugView))]
public sealed class OqlJoinExpression : OqlSourceBindingExpression
{
    /// <summary>Initializes a key lookup binding.</summary>
    /// <param name="collection">The right-hand stored collection name.</param>
    /// <param name="variable">The required right-hand alias.</param>
    /// <param name="key">The scalar key expression over earlier bindings.</param>
    /// <param name="isLeft">Whether to preserve unmatched inputs.</param>
    /// <param name="isNest">Whether to bind an array of matching documents.</param>
    /// <param name="location">The source span.</param>
    public OqlJoinExpression(string collection, string variable, OqlExpression key, bool isLeft = false, bool isNest = false, Location? location = null)
        : base(key, variable, isLeft, location)
    { Collection = collection; IsNest = isNest; }

    /// <summary>Gets the right-hand stored collection name.</summary>
    public string Collection { get; }
    /// <summary>Gets whether the result binding is an array.</summary>
    public bool IsNest { get; }

    private sealed class DebugView
    {
        private readonly OqlJoinExpression _target;
        public DebugView(OqlJoinExpression target) { _target = target; }
        public string Collection => _target.Collection;
        public string Variable => _target.Variable;
        public OqlExpression Expression => _target.Expression;
        public bool IsLeft => _target.IsLeft;
        public bool IsNest => _target.IsNest;
        public Location? Location => _target.Location;
    }
}
