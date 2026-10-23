using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An OQL statement expression tree and its parse diagnostics.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlQueryStatement")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlQueryStatement.DebugView))]
public sealed class OqlQueryStatement : QueryStatement
{
    /// <summary>Initializes a new instance of the <see cref="OqlQueryStatement"/> class.</summary>
    /// <param name="expression">The parsed statement; consult diagnostics before planning it.</param>
    public OqlQueryStatement(OqlExpression expression)
    {
        OqlExpression = expression;
    }

    /// <summary>Gets the parsed OQL statement expression.</summary>
    public OqlExpression OqlExpression { get; }

    /// <inheritdoc />
    public override QueryExpression Expression => OqlExpression;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlQueryStatement _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlQueryStatement value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression OqlExpression => _target.OqlExpression;
        public global::Assimalign.Cohesion.Database.Language.QueryExpression Expression => _target.Expression;
    }
}
