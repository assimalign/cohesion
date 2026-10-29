using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlUpdateExpression ParseUpdate()
    {
        var start = Current;
        Advance();
        string collection = CollectionName();
        string? alias = null;
        if (Take("AS")) { alias = Identifier(); }
        else if (IsUpdateAlias()) { alias = Identifier(); }
        RejectDmlUnnest();
        List<OqlUpdateAssignment> assignments = [];
        List<OqlPathExpression> unset = [];
        if (Take("SET"))
        {
            do
            {
                var assignmentStart = Current;
                var path = ParseDocumentPath();
                Expect("=");
                var value = ParseExpression();
                assignments.Add(new OqlUpdateAssignment(path, value, Span(assignmentStart, Previous)));
            } while (!Failed && Take(TokenType.Comma));
        }
        if (Take("UNSET"))
        {
            do { unset.Add(ParseDocumentPath()); } while (!Failed && Take(TokenType.Comma));
        }
        RejectDmlUnnest();
        if (!Failed && assignments.Count == 0 && unset.Count == 0)
        {
            Error("CD00002", ErrorMessages.OqlQueryParserParseUpdateVoid, Current);
        }
        OqlExpression? predicate = !Failed && Take("WHERE") ? ParseExpression() : null;
        RejectDmlUnnest();
        // UPDATE paging remains unsupported independently of SELECT capabilities.
        if (!Failed && (Is("LIMIT") || Is("OFFSET")))
        {
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("UPDATE " + Current.Text.ToUpperInvariant(), Profile.Language, Span(Current, Current)));
        }
        var returning = ParseReturning();
        return new OqlUpdateExpression(collection, alias, assignments.AsReadOnly(), unset.AsReadOnly(), predicate, Span(start, Previous), returning);
    }

    private bool IsUpdateAlias() => Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function &&
        (!IsSourceBindingClause() || _position + 1 < _tokens.Count &&
            (IsWord(_tokens[_position + 1], "SET") || IsWord(_tokens[_position + 1], "UNSET"))) &&
        !Is("LIMIT") && !(Is("USE") && _position + 1 < _tokens.Count && IsWord(_tokens[_position + 1], "KEYS")) &&
        (!IsReturningClause() || Is("RETURNING") && _position + 1 < _tokens.Count &&
            (IsWord(_tokens[_position + 1], "SET") || IsWord(_tokens[_position + 1], "UNSET")));

    private OqlDeleteExpression ParseDelete()
    {
        var start = Current;
        Advance();
        Expect("FROM");
        string collection = CollectionName();
        string? alias = null;
        if (Take("AS"))
        {
            alias = Identifier();
        }
        else if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function &&
            (!IsSourceBindingClause() || _position + 1 < _tokens.Count &&
                (IsWord(_tokens[_position + 1], "WHERE") || _tokens[_position + 1].Type is TokenType.Eof or TokenType.Semicolon)) &&
            !Is("LIMIT") && !IsReturningClause())
        {
            alias = Identifier();
        }
        RejectDmlUnnest();
        OqlExpression? predicate = !Failed && Take("WHERE") ? ParseExpression() : null;
        RejectDmlUnnest();
        // DELETE paging remains unsupported independently of SELECT capabilities.
        if (!Failed && (Is("LIMIT") || Is("OFFSET")))
        {
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("DELETE " + Current.Text.ToUpperInvariant(), Profile.Language, Span(Current, Current)));
        }
        var returning = ParseReturning();
        return new OqlDeleteExpression(collection, alias, predicate, Span(start, Previous), returning);
    }

    private void RejectDmlUnnest()
    {
        if (Failed || !IsSourceBindingClause()) { return; }
        if (Take("LEFT")) { Take("OUTER"); }
        else { Take("INNER"); }
        _diagnostics.Add(QueryDiagnostics.UnsupportedClause(Current.Text.ToUpperInvariant() + " in UPDATE or DELETE", Profile.Language, Span(Current, Current)));
    }

    private OqlExpression ParseWrite(bool upsert = false)
    {
        var start = Current;
        Advance();
        Expect("INTO");
        string collection = CollectionName();
        string? alias = Take("AS") ? Identifier() : null;
        string verb = upsert ? "UPSERT" : "INSERT";
        OqlReturningExpression? returning = null;
        string? keyName = null;
        string? valueName = null;
        if (!Take(TokenType.LeftParen))
        {
            Error("CD00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParseWriteVoid, verb), Current);
            return Build([], null);
        }
        Expect("KEY");
        bool valuesHeader = Take(TokenType.Comma);
        if (valuesHeader)
        {
            Expect("VALUE");
        }
        else
        {
            keyName = Identifier();
            if (RejectOptions()) { return Build([], null); }
            if (Take(TokenType.Comma))
            {
                Expect("VALUE");
                valueName = Identifier();
            }
        }
        if (RejectOptions()) { return Build([], null); }
        Expect(TokenType.RightParen, "')'");
        List<(OqlExpression Key, OqlExpression Value)> values = [];
        OqlSelectExpression? select = null;
        if (valuesHeader)
        {
            Expect("VALUES");
            do
            {
                Expect(TokenType.LeftParen, "'('");
                var key = ParseExpression();
                Expect(TokenType.Comma, "',' followed by a VALUE expression");
                var value = ParseExpression();
                Expect(TokenType.RightParen, "')'");
                values.Add((key, value));
            } while (!Failed && Take(TokenType.Comma));
            if (!Failed && (Is("LIMIT") || Is("OFFSET")))
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause(verb + " VALUES " + Current.Text.ToUpperInvariant(), Profile.Language, Span(Current, Current)));
            }
        }
        else if (Is("SELECT"))
        {
            select = ParseSelect();
        }
        else
        {
            Error("CD00002", ErrorMessages.OqlQueryParserParseWriteVoid2, Current);
        }
        returning = ParseReturning();
        return Build(values.AsReadOnly(), select);

        bool RejectOptions()
        {
            if (Current.Type != TokenType.Comma || _position + 1 >= _tokens.Count || !IsWord(_tokens[_position + 1], "OPTIONS")) { return false; }
            Advance();
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("OPTIONS", Profile.Language, Span(Current, Current)));
            return true;
        }

        OqlExpression Build(IReadOnlyList<(OqlExpression Key, OqlExpression Value)> rows, OqlSelectExpression? source)
            => upsert
                ? new OqlUpsertExpression(collection, alias, rows, source, keyName, valueName, Span(start, Previous), returning)
                : new OqlInsertExpression(collection, alias, rows, source, keyName, valueName, Span(start, Previous), returning);
    }
    private OqlReturningExpression? ParseReturning()
    {
        if (Failed || !Is("RETURNING")) { return null; }
        var start = Current;
        Advance();
        bool value = Take("RAW") || Take("VALUE");
        List<OqlProjection> projections = [];
        do
        {
            var expression = ParseExpression();
            if (value && expression is OqlStarExpression)
            { Error("CD00002", ErrorMessages.ReturningValueInvalid, Previous); }
            string? alias = !value && Take("AS") ? Identifier(allowKeyword: true) : null;
            projections.Add(new OqlProjection(expression, alias));
        } while (!value && !Failed && Take(TokenType.Comma));
        return new OqlReturningExpression(projections.AsReadOnly(), value, Span(start, Previous));
    }

}
