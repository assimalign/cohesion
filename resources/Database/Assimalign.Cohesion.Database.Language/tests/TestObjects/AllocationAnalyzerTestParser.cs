namespace Assimalign.Cohesion.Database.Language.Tests;

internal sealed class AllocationAnalyzerTestParser : QueryParser
{
    private static readonly QueryLanguageProfile _profile = new("TEST", [], [], []);
    private readonly QueryStatement _statement = new AnalyzerTestStatement();

    internal AllocationAnalyzerTestParser(QueryParserOptions options) : base(options) { }

    protected override QueryLanguageProfile Profile => _profile;

    protected override QueryStatement ParseCore(TokenLexer lexer) => _statement;
}
