using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A GQL statement and its parse diagnostics.</summary>
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
}
