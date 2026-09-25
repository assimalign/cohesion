using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An OQL statement expression tree and its parse diagnostics.</summary>
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
}
