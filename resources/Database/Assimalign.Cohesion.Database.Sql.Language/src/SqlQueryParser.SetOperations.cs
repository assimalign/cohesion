using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private bool StartsQuery(TokenLexer lexer)
    {
        int depth = _parenthesisDepth;
        while (lexer.Current.Type == TokenType.LeftParen)
        {
            // Classification must not rescan an unbounded run at every expression
            // parenthesis. Past the budget, let the guarded query parser report SQL0006.
            if (++depth > _nestingLimit) { return true; }
            if (!AdvancePastComments(ref lexer)) { return false; }
        }
        return IsKeyword(ref lexer, "SELECT") || IsKeyword(ref lexer, "WITH");
    }

    private SqlQueryBodyExpression ParseQueryExpression(ref TokenLexer lexer)
    {
        var query = ParseSetLevel(ref lexer, intersect: false);
        ParseQueryTail(ref lexer, query);
        return query;
    }

    // PostgreSQL src/backend/parser/gram.y:845-846: INTERSECT binds tighter;
    // UNION and EXCEPT share a left-associative rung.
    private SqlQueryBodyExpression ParseSetLevel(ref TokenLexer lexer, bool intersect)
    {
        var left = intersect ? ParseQueryOperand(ref lexer) : ParseSetLevel(ref lexer, intersect: true);
        while (intersect ? IsKeyword(ref lexer, "INTERSECT") : IsKeyword(ref lexer, "UNION") || IsKeyword(ref lexer, "EXCEPT"))
        {
            var operation = IsKeyword(ref lexer, "UNION") ? SqlSetOperator.Union
                : IsKeyword(ref lexer, "EXCEPT") ? SqlSetOperator.Except : SqlSetOperator.Intersect;
            int start = left.Location?.Start ?? lexer.Current.Position;
            Advance(ref lexer);
            bool isAll = IsKeyword(ref lexer, "ALL");
            if (isAll || IsKeyword(ref lexer, "DISTINCT")) { Advance(ref lexer); }
            if (IsWord(ref lexer, "CORRESPONDING"))
            {
                RequireSkippedConstruct(lexer.Current.Position, CurrentTokenEnd(ref lexer), "CORRESPONDING");
                ConsumeRemaining(ref lexer);
                return left;
            }
            if (!TryOpenChain(ref lexer, left.ExpressionDepth) || !TryEnterOperand(ref lexer)) { return left; }
            SqlQueryBodyExpression right;
            try { right = intersect ? ParseQueryOperand(ref lexer) : ParseSetLevel(ref lexer, intersect: true); }
            finally { _expressionDepth--; }
            left = new SqlSetOperationExpression(left, right, operation, isAll, Location.Create(1, 1, start, _lastTokenEnd));
            if (_expressionDepth + left.ExpressionDepth > _nestingLimit)
            {
                RejectNesting(ref lexer, start, _lastTokenEnd, ExpressionTooDeepMessage);
                return left;
            }
            Deepen(_expressionDepth + left.ExpressionDepth);
        }
        return left;
    }

    private SqlQueryBodyExpression ParseQueryOperand(ref TokenLexer lexer)
    {
        if (IsKeyword(ref lexer, "SELECT")) { return ParseSelect(ref lexer); }
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            int start = lexer.Current.Position;
            Advance(ref lexer);
            if (!TryEnterParenthesis(ref lexer, start)) { return EmptyWithQuery(); }
            try
            {
                var query = ParseQueryExpression(ref lexer);
                Expect(ref lexer, TokenType.RightParen, "')' after the query operand");
                return query;
            }
            finally { _parenthesisDepth--; }
        }
        AddSyntaxDiagnostic(ref lexer, "Expected SELECT or a parenthesized query after the set operator.");
        return EmptyWithQuery();
    }

    private void ParseQueryTail(ref TokenLexer lexer, SqlQueryBodyExpression query)
    {
        SqlExpression? limit = query.Limit;
        SqlLimitSyntaxKind? limitSyntaxKind = query.LimitSyntaxKind;

        // ORDER BY
        var orderBy = new List<SqlOrderByColumn>();
        if (query.OrderBy.Count > 0 && IsKeyword(ref lexer, "ORDER"))
        {
            AddSyntaxDiagnostic(ref lexer, "A query accepts only one ORDER BY clause.");
        }
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ORDER"))
        {
            Advance(ref lexer);
            ExpectKeyword(ref lexer, "BY", "BY after ORDER");

            orderBy.Add(ParseOrderByColumn(ref lexer));
            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                orderBy.Add(ParseOrderByColumn(ref lexer));
            }
        }

        // PostgreSQL permits either limit/offset order; rendering uses ISO OFFSET ... FETCH.
        SqlExpression? offset = query.Offset;
        while (IsKeyword(ref lexer, "LIMIT") || IsKeyword(ref lexer, "OFFSET") || IsKeyword(ref lexer, "FETCH"))
        {
            string clause = CurrentText(ref lexer).ToUpperInvariant();
            int start = lexer.Current.Position;
            int end = start + lexer.Current.Value.Length;
            if (clause == "OFFSET" ? offset is not null || limitSyntaxKind == SqlLimitSyntaxKind.Top : limit is not null)
            {
                AddSyntaxDiagnostic(start, end, "A SELECT accepts one row limit and one OFFSET; TOP cannot combine with LIMIT, OFFSET or FETCH, and LIMIT cannot combine with FETCH.");
            }
            Advance(ref lexer);
            if (clause == "OFFSET")
            {
                offset = ParsePaginationExpression(ref lexer, "OFFSET");
                if (IsKeyword(ref lexer, "ROW") || IsKeyword(ref lexer, "ROWS")) { Advance(ref lexer); }
            }
            else if (clause == "LIMIT")
            {
                limit = ParsePaginationExpression(ref lexer, "LIMIT");
                limitSyntaxKind = SqlLimitSyntaxKind.Limit;
            }
            else
            {
                if (IsWord(ref lexer, "FIRST") || IsKeyword(ref lexer, "NEXT")) { Advance(ref lexer); }
                else { AddSyntaxDiagnostic(ref lexer, "Expected FIRST or NEXT after FETCH."); }
                limit = IsKeyword(ref lexer, "ROW") || IsKeyword(ref lexer, "ROWS")
                    ? new SqlLiteralExpression("1", SqlLiteralType.Integer, Location.Create(1, 1, start, end))
                    : ParseFetchCount(ref lexer);
                limitSyntaxKind = SqlLimitSyntaxKind.Fetch;
                RejectPagingSuffix(ref lexer, "FETCH");
                if (IsKeyword(ref lexer, "ROW") || IsKeyword(ref lexer, "ROWS")) { Advance(ref lexer); }
                else { AddSyntaxDiagnostic(ref lexer, "Expected ROW or ROWS after the FETCH count."); }
                if (!RejectPagingSuffix(ref lexer, "FETCH"))
                {
                    ExpectKeyword(ref lexer, "ONLY", "ONLY after FETCH ... ROW or ROWS");
                }
            }
        }

        if (orderBy.Count > 0) { query.OrderBy = orderBy; }
        query.Limit = limit;
        query.LimitSyntaxKind = limitSyntaxKind;
        if (offset is not null) { query.Offset = offset; }
        query.IncludeTailDepth();
    }
}
