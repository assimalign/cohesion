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
        if (!Failed && assignments.Count == 0 && unset.Count == 0)
        {
            Error("OQL0002", "UPDATE requires at least one SET assignment or UNSET path.", Current);
        }
        OqlExpression? predicate = !Failed && Take("WHERE") ? ParseExpression() : null;
        // UPDATE owns this refusal even after SELECT gains LIMIT.
        if (!Failed && Is("LIMIT"))
        {
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("LIMIT", Profile.Language, Span(Current, Current)));
        }
        return new OqlUpdateExpression(collection, alias, assignments.AsReadOnly(), unset.AsReadOnly(), predicate, Span(start, Previous));
    }

    private bool IsUpdateAlias() => Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function &&
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
            !Is("LIMIT") && !IsReturningClause())
        {
            alias = Identifier();
        }
        OqlExpression? predicate = Take("WHERE") ? ParseExpression() : null;
        // DELETE LIMIT remains deferred even after SELECT gains row limits.
        if (!Failed && Is("LIMIT"))
        {
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("LIMIT", Profile.Language, Span(Current, Current)));
        }
        return new OqlDeleteExpression(collection, alias, predicate, Span(start, Previous));
    }

    private OqlExpression ParseWrite(bool upsert = false)
    {
        var start = Current;
        Advance();
        Expect("INTO");
        string collection = CollectionName();
        string? alias = Take("AS") ? Identifier() : null;
        string verb = upsert ? "UPSERT" : "INSERT";
        string? keyName = null;
        string? valueName = null;
        if (!Take(TokenType.LeftParen))
        {
            Error("OQL0002", $"{verb} requires an explicit KEY header; document ids are not generated.", Current);
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
        }
        else if (Is("SELECT"))
        {
            select = ParseSelect();
        }
        else
        {
            Error("OQL0002", "Expected SELECT after the KEY output binding.", Current);
        }
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
                ? new OqlUpsertExpression(collection, alias, rows, source, keyName, valueName, Span(start, Previous))
                : new OqlInsertExpression(collection, alias, rows, source, keyName, valueName, Span(start, Previous));
    }
}
