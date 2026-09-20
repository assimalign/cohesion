using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An OQL statement expression tree and its parse diagnostics.</summary>
/// <param name="expression">The parsed statement; consult diagnostics before planning it.</param>
public sealed class OqlQueryStatement(OqlExpression expression) : QueryStatement
{
    /// <summary>Gets the parsed OQL statement expression.</summary>
    public OqlExpression OqlExpression { get; } = expression;

    /// <inheritdoc />
    public override QueryExpression Expression => OqlExpression;
}
