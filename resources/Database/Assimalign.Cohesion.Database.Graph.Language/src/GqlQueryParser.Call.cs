using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlCallStage ParseCall()
    {
        var start = Current;
        bool optional = Take("OPTIONAL");
        Expect("CALL");
        List<string>? imports = null;
        bool all = false;
        if (Take(TokenType.LeftParen))
        {
            imports = [];
            all = Take(TokenType.Asterisk);
            if (!all && Current.Type != TokenType.RightParen)
            { do { imports.Add(Identifier()); } while (!Failed && Take(TokenType.Comma)); }
            Expect(TokenType.RightParen, "')'");
        }
        Expect(TokenType.LeftBrace, "'{'");
        if (Failed || !HasStackToNest(start)) { return new GqlCallStage(EmptyExpression(), Location: Span(start, Previous)); }
        _callDepth++;
        GqlQueryExpression body;
        try { body = Current.Type == TokenType.RightBrace ? EmptyExpression() : ParseNextQuery(hasInput: all || imports is null || imports.Count != 0); }
        finally { _callDepth--; }
        body.SetStatementText(_source);
        Expect(TokenType.RightBrace, "'}'");
        if (!Failed && Take("IN")) { Unsupported("CALL IN TRANSACTIONS", Previous); }
        return new GqlCallStage(body, imports?.AsReadOnly(), all, optional, Span(start, Previous));
    }

    // Capability scanning gives each inline body its own clause and value context.
    private bool ScanInlineCall(ref int index, int end)
    {
        int cursor = index + 1;
        if (cursor < end && _tokens[cursor].Type == TokenType.LeftParen)
        {
            int depth = 1;
            while (++cursor < end && depth != 0)
            {
                if (_tokens[cursor].Type == TokenType.LeftParen) { depth++; }
                if (_tokens[cursor].Type == TokenType.RightParen) { depth--; }
            }
        }
        if (cursor >= end || _tokens[cursor].Type != TokenType.LeftBrace)
        {
            // A scope prefix already identifies inline CALL; let parsing diagnose a missing body.
            if (_tokens[index + 1].Type != TokenType.LeftParen) { return false; }
            index = System.Math.Min(cursor - 1, end - 1);
            return true;
        }
        if (!HasStackToNest(_tokens[index])) { index = end; return true; }
        int first = ++cursor;
        int braces = 1;
        while (cursor < end)
        {
            if (_tokens[cursor].Type == TokenType.LeftBrace) { braces++; }
            if (_tokens[cursor].Type == TokenType.RightBrace && --braces == 0) { break; }
            if (_tokens[cursor].Type == TokenType.Eof) { break; }
            cursor++;
        }
        FindUnsupported(first, cursor);
        index = cursor;
        if (IsWordAt(cursor + 1, "IN")) { UnsupportedConstruct("CALL IN TRANSACTIONS", _tokens[cursor + 1], _tokens[cursor + 1]); }
        return true;
    }
}
