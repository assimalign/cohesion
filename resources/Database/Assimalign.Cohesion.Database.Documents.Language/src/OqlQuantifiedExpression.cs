using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An ANY, SOME or EVERY predicate over one array binding.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlQuantifiedExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlQuantifiedExpression.DebugView))]
public sealed class OqlQuantifiedExpression : OqlExpression
{
    /// <summary>Initializes a quantified predicate; SOME is normalized to ANY.</summary>
    /// <param name="variable">The variable visible inside SATISFIES.</param>
    /// <param name="collection">The array expression evaluated in the enclosing scope.</param>
    /// <param name="predicate">The SATISFIES expression.</param>
    /// <param name="isEvery">Whether every element must satisfy the predicate.</param>
    /// <param name="location">The source span.</param>
    public OqlQuantifiedExpression(string variable, OqlExpression collection, OqlExpression predicate, bool isEvery = false, Location? location = null) : base(location)
    {
        Variable = variable;
        Collection = collection;
        Predicate = predicate;
        IsEvery = isEvery;
    }

    /// <summary>Gets the inner variable name.</summary>
    public string Variable { get; }
    /// <summary>Gets the collection expression.</summary>
    public OqlExpression Collection { get; }
    /// <summary>Gets the predicate.</summary>
    public OqlExpression Predicate { get; }
    /// <summary>Gets whether this is EVERY rather than ANY or SOME.</summary>
    public bool IsEvery { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlQuantifiedExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlQuantifiedExpression value)
        {
            _target = value;
        }

        public string Variable => _target.Variable;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Collection => _target.Collection;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Predicate => _target.Predicate;
        public bool IsEvery => _target.IsEvery;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
