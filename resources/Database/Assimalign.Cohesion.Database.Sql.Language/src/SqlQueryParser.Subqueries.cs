using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private const int maximumSubqueryDepth = 32;
    private int _subqueryDepth;
    private int _paginationDepth;
    private string _paginationClause = "LIMIT or OFFSET";

    private void SkipQueryRemainder(ref TokenLexer lexer)
    {
        int depth = 0;
        while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
        {
            if (lexer.Current.Type == TokenType.RightParen && depth == 0) { break; }
            if (lexer.Current.Type == TokenType.LeftParen) { depth++; }
            else if (lexer.Current.Type == TokenType.RightParen) { depth--; }
            Advance(ref lexer);
        }
    }

    private SqlQueryBodyExpression ParseSubquery(ref TokenLexer lexer)
    {
        if (IsKeyword(ref lexer, "WITH"))
        {
            RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length,
                "WITH inside a subquery or derived table");
            SkipQueryRemainder(ref lexer);
            return EmptyWithQuery();
        }

        if (_paginationDepth > 0)
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserParseSubqueryVoid, _paginationClause));
        }

        if (_subqueryDepth >= maximumSubqueryDepth)
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserParseSubqueryVoid2, maximumSubqueryDepth));

            // Skip this query iteratively, leaving its closing parenthesis to the
            // caller. Never recurse into input beyond the declared depth limit.
            int parentheses = 0;
            while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.Semicolon)
            {
                if (lexer.Current.Type == TokenType.RightParen && parentheses == 0)
                {
                    break;
                }
                if (lexer.Current.Type == TokenType.LeftParen)
                {
                    parentheses++;
                }
                if (lexer.Current.Type == TokenType.RightParen)
                {
                    parentheses--;
                }
                Advance(ref lexer);
            }

            return new SqlSelectExpression([], [], null, [], null, [],
                null, null, false, null, null);
        }

        // The subquery node (scalar, IN, EXISTS or quantified) encloses the query's clauses, so they parse
        // one level deeper; the expression limit spans the whole statement (#1151).
        if (!TryEnterOperand(ref lexer))
        {
            return new SqlSelectExpression([], [], null, [], null, [],
                null, null, false, null, null);
        }

        _subqueryDepth++;
        try
        {
            return ParseQueryExpression(ref lexer);
        }
        finally
        {
            _subqueryDepth--;
            _expressionDepth--;
        }
    }

    private SqlExpression ParsePaginationExpression(ref TokenLexer lexer, string clause, bool primaryOnly = false)
    {
        string previousClause = _paginationClause;
        _paginationClause = clause;
        _paginationDepth++;
        try
        {
            return primaryOnly ? ParsePrimary(ref lexer) : ParseExpression(ref lexer);
        }
        finally
        {
            _paginationDepth--;
            _paginationClause = previousClause;
        }
    }

    private static bool TryFindUnsupportedSubqueryForm(
        TokenLexer lexer,
        string firstToken,
        string? previousToken,
        string? previousPreviousToken,
        bool insertValues,
        bool dmlWhere,
        bool returning,
        bool onConflictUpdate,
        bool followsFromComma,
        out string clause)
    {
        clause = string.Empty;
        if (lexer.Current.Type is not (TokenType.Identifier or TokenType.Keyword))
        {
            return false;
        }

        string token = CurrentText(ref lexer);
        if (token.Equals("LATERAL", StringComparison.OrdinalIgnoreCase) &&
                 (string.Equals(previousToken, "FROM", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(previousToken, "JOIN", StringComparison.OrdinalIgnoreCase) || previousToken == "," && followsFromComma))
        {
            clause = "LATERAL subquery";
        }
        else if (lexer.Current.Type == TokenType.Keyword &&
                 token.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && previousToken is not null)
        {
            if (onConflictUpdate)
            {
                clause = "SUBQUERY in ON CONFLICT DO UPDATE";
            }
            else if (returning)
            {
                clause = "SUBQUERY in RETURNING";
            }
            else if (!firstToken.Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
                !firstToken.Equals("INSERT", StringComparison.OrdinalIgnoreCase) && !dmlWhere)
            {
                clause = $"SUBQUERY in {firstToken.ToUpperInvariant()}";
            }
            else if (insertValues)
            {
                clause = "SUBQUERY in INSERT VALUES; use INSERT ... SELECT";
            }
        }

        return clause.Length > 0;
    }

}
