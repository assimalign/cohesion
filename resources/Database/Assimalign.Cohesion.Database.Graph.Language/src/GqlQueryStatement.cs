using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A GQL statement and its parse diagnostics.</summary>
/// <param name="expression">The parsed expression; check diagnostics before planning.</param>
public sealed class GqlQueryStatement(GqlQueryExpression expression) : QueryStatement
{
    /// <summary>Gets the database-scoped query expression.</summary>
    public GqlQueryExpression GqlExpression { get; } = expression;

    /// <inheritdoc />
    public override QueryExpression Expression => GqlExpression;
}
