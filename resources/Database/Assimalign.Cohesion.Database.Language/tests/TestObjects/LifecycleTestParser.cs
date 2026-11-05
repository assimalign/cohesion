using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language.Tests;

internal sealed class LifecycleTestParser : QueryParser
{
    private static readonly QueryLanguageProfile _profile = new("TEST", [], [], []);
    private readonly List<string> _order;

    internal LifecycleTestParser(QueryParserOptions options, List<string> order) : base(options, serializeCalls: true)
    {
        _order = order;
    }

    protected override QueryLanguageProfile Profile => _profile;

    protected override QueryStatement ParseCore(TokenLexer lexer, ReadOnlySpan<char> query)
    {
        _order.Add("parse:" + query.ToString());
        return new AnalyzerTestStatement();
    }
}
