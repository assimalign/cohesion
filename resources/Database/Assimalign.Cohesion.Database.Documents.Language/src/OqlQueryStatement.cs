using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An OQL expression tree and its parse diagnostics.</summary>
/// <param name="expression">The parsed query; consult diagnostics before planning it.</param>
public sealed class OqlQueryStatement(OqlSelectExpression expression) : QueryStatement
{
    /// <summary>Gets the parsed OQL query.</summary>
    public OqlSelectExpression OqlExpression { get; } = expression;

    /// <inheritdoc />
    public override QueryExpression Expression => OqlExpression;
}
