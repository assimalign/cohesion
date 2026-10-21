namespace Assimalign.Cohesion.Database.Language.Tests;

internal sealed class AnalyzerTestStatement : QueryStatement
{
    public override QueryExpression Expression { get; } = new AnalyzerTestExpression();
}
