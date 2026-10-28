using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A Boolean existence test over a correlated, lexically isolated graph pattern.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlExistsExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GqlExistsExpression.DebugView))]
public sealed class GqlExistsExpression : GqlExpression
{
    /// <summary>Initializes an existence predicate.</summary>
    /// <param name="matchClauses">The nonempty, source-ordered MATCH clauses of the inner pattern.</param>
    /// <param name="location">The optional source span.</param>
    public GqlExistsExpression(IReadOnlyList<GqlMatchClause> matchClauses, Location? location = null) : base(location)
    {
        MatchClauses = matchClauses;
    }

    /// <summary>Gets the inner clauses. Outer bindings are arguments; new bindings remain local.</summary>
    public IReadOnlyList<GqlMatchClause> MatchClauses { get; }

    /// <summary>Renders the predicate with explicit MATCH keywords.</summary>
    /// <returns>Equivalent GQL syntax.</returns>
    public override string ToString() => GqlPathRenderer.RenderExpression(this);

    private sealed class DebugView
    {
        private readonly GqlExistsExpression _target;
        public DebugView(GqlExistsExpression value) { _target = value; }
        public IReadOnlyList<GqlMatchClause> MatchClauses => _target.MatchClauses;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
