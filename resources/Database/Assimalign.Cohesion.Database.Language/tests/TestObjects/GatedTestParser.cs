using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.Database.Language.Tests;

internal sealed class GatedTestParser : QueryParser
{
    private static readonly QueryLanguageProfile _profile = new("TEST", [], [], []);
    private readonly ConcurrentQueue<string> _order;

    internal GatedTestParser(QueryParserOptions options, ConcurrentQueue<string> order)
        : base(options, serializeCalls: true)
    {
        _order = order;
    }

    protected override QueryLanguageProfile Profile => _profile;

    protected override QueryStatement ParseCore(TokenLexer lexer, ReadOnlySpan<char> query)
    {
        _order.Enqueue("parse:" + query.ToString());
        return new AnalyzerTestStatement();
    }
}
