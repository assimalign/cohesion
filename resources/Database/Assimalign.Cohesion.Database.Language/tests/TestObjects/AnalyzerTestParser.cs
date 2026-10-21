using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Language.Tests;

internal sealed class AnalyzerTestParser : QueryParser
{
    private static readonly QueryLanguageProfile _profile = new("TEST", [], [], []);

    internal AnalyzerTestParser(QueryParserOptions options) : base(options) { }

    protected override QueryLanguageProfile Profile => _profile;

    protected override QueryStatement ParseCore(TokenLexer lexer) => new AnalyzerTestStatement();
}
