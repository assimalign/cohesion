using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A GQL statement and its parse diagnostics.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlQueryStatement")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlQueryStatement.DebugView))]
public sealed class GqlQueryStatement : QueryStatement
{
    /// <summary>Initializes a new instance of the <see cref="GqlQueryStatement"/> class.</summary>
    /// <param name="expression">The parsed expression; check diagnostics before planning.</param>
    public GqlQueryStatement(GqlQueryExpression expression)
    {
        GqlExpression = expression;
    }

    /// <summary>Gets the database-scoped query expression.</summary>
    public GqlQueryExpression GqlExpression { get; }

    /// <inheritdoc />
    public override QueryExpression Expression => GqlExpression;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlQueryStatement _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlQueryStatement value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlQueryExpression GqlExpression => _target.GqlExpression;
        public global::Assimalign.Cohesion.Database.Language.QueryExpression Expression => _target.Expression;
    }
}
