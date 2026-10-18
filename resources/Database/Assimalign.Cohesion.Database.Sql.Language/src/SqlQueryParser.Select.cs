using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

public sealed partial class SqlQueryParser
{
    private SqlSelectExpression ParseSelect(ref TokenLexer lexer)
    {
        var pos = lexer.Current.Position;
        Advance(ref lexer); // consume SELECT

        // DISTINCT
        bool isDistinct = false;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "DISTINCT"))
        {
            isDistinct = true;
            Advance(ref lexer);
        }

        SqlExpression? limit = null;
        SqlLimitSyntaxKind? limitSyntaxKind = null;
        if (IsKeyword(ref lexer, "TOP") && !(TryPeekToken(lexer, out string topNext, out _) && topNext == "."))
        {
            Advance(ref lexer);
            limit = ParseTopCount(ref lexer);
            limitSyntaxKind = SqlLimitSyntaxKind.Top;
            RejectPagingSuffix(ref lexer, "TOP");
        }

        // Parse SELECT column list
        var columns = ParseSelectColumns(ref lexer);

        // FROM
        var from = new List<SqlFromItem>();
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "FROM"))
        {
            Advance(ref lexer);
            do
            {
                var table = ParseRelationReference(ref lexer);
                from.Add(new SqlFromItem(table, ParseJoinClauses(ref lexer)));
                if (IsAtEnd(ref lexer) || lexer.Current.Type != TokenType.Comma)
                {
                    break;
                }
                Advance(ref lexer);
            } while (true);
        }

        // WHERE
        SqlExpression? where = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "WHERE"))
        {
            Advance(ref lexer);
            where = ParseExpression(ref lexer);
        }

        // GROUP BY
        var groupBy = new List<SqlExpression>();
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "GROUP"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "BY"))
            {
                Advance(ref lexer);
            }
            else
            {
                AddSyntaxDiagnostic(ref lexer, "Expected BY after GROUP.");
            }

            while (true)
            {
                if (IsAtEnd(ref lexer) || lexer.Current.Type is TokenType.Semicolon or TokenType.RightParen or TokenType.Comma ||
                    IsStatementBoundaryKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, "Expected a grouping expression after GROUP BY or ','.");
                    break;
                }

                groupBy.Add(ParseGroupingExpression(ref lexer));
                if (IsAtEnd(ref lexer) || lexer.Current.Type != TokenType.Comma)
                {
                    break;
                }
                Advance(ref lexer);
            }
        }

        // HAVING
        SqlExpression? having = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "HAVING"))
        {
            Advance(ref lexer);
            if (IsAtEnd(ref lexer) || lexer.Current.Type is TokenType.Semicolon or TokenType.RightParen ||
                IsStatementBoundaryKeyword(ref lexer))
            {
                AddSyntaxDiagnostic(ref lexer, "Expected a predicate after HAVING.");
            }
            else
            {
                having = ParseExpression(ref lexer);
            }
        }

        // ORDER BY
        var orderBy = new List<SqlOrderByColumn>();
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
        SqlExpression? offset = null;
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

        // Consume trailing semicolon (don't advance past it so ParseCore picks it up)
        // The semicolon is tracked by TrackToken when we advance

        return new SqlSelectExpression(
            columns, from, where, groupBy, having, orderBy,
            limit, offset, isDistinct, null,
            Location.Create(1, 1, pos, _lastTokenEnd), limitSyntaxKind);
    }

    private SqlExpression ParseFetchCount(ref TokenLexer lexer)
    {
        // PostgreSQL src/backend/parser/gram.y:13674-13696: c_expr or a signed numeric
        // constant. A compound expression needs parentheses; a sign cannot prefix a name.
        var next = lexer;
        if (lexer.Current.Type == TokenType.Minus && AdvancePastComments(ref next) &&
            next.Current.Type is TokenType.Integer or TokenType.Float)
        {
            int position = lexer.Current.Position;
            Advance(ref lexer);
            var operand = ParseOperand(ref lexer, OperandRule.Primary);
            return new SqlUnaryExpression(operand, SqlUnaryOperator.Negate,
                Location.Create(1, 1, position, _lastTokenEnd));
        }
        return ParsePaginationExpression(ref lexer, "FETCH", primaryOnly: true);
    }

    private SqlExpression ParseTopCount(ref TokenLexer lexer)
    {
        // Exactly one token without parentheses: TOP 5 * must leave the projection's star.
        if (lexer.Current.Type is TokenType.LeftParen or TokenType.Integer or TokenType.Parameter)
        {
            return ParsePaginationExpression(ref lexer, "TOP", primaryOnly: true);
        }
        AddSyntaxDiagnostic(ref lexer, "TOP requires one integer literal or parameter, or a parenthesized count expression.");
        return new SqlLiteralExpression("NULL", SqlLiteralType.Null,
            Location.Create(1, 1, lexer.Current.Position, lexer.Current.Position));
    }

    private bool RejectPagingSuffix(ref TokenLexer lexer, string clause)
    {
        if (IsWord(ref lexer, "PERCENT"))
        {
            RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length,
                $"{clause} PERCENT");
            Advance(ref lexer);
            return true;
        }
        if (IsKeyword(ref lexer, "WITH") && TryPeekToken(lexer, out string next, out int end) &&
            next.Equals("TIES", StringComparison.OrdinalIgnoreCase))
        {
            RequireSkippedConstruct(lexer.Current.Position, end, $"{clause} WITH TIES");
            Advance(ref lexer);
            Advance(ref lexer);
            return true;
        }
        return false;
    }

    private void RejectDmlTop(ref TokenLexer lexer)
    {
        if (!IsKeyword(ref lexer, "TOP")) { return; }
        RequireSkippedConstruct(lexer.Current.Position, lexer.Current.Position + lexer.Current.Value.Length, "DML TOP");
        Advance(ref lexer);
        ParseTopCount(ref lexer);
        RejectPagingSuffix(ref lexer, "TOP");
    }

    private List<SqlSelectColumn> ParseSelectColumns(ref TokenLexer lexer)
    {
        var columns = new List<SqlSelectColumn>();

        if (IsAtEnd(ref lexer))
        {
            return columns;
        }

        columns.Add(ParseSingleSelectColumn(ref lexer));

        while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
        {
            Advance(ref lexer);
            columns.Add(ParseSingleSelectColumn(ref lexer));
        }

        return columns;
    }

    private SqlSelectColumn ParseSingleSelectColumn(ref TokenLexer lexer)
    {
        var expr = ParseExpression(ref lexer);
        string? alias = null;

        // Check for AS alias or just alias
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "AS"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                alias = CurrentIdentifierText(ref lexer);
                Advance(ref lexer);
            }
        }
        else if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer) &&
                 !IsStatementBoundaryKeyword(ref lexer))
        {
            // Check it's not a keyword that starts the next clause
            alias = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }

        return new SqlSelectColumn(expr, alias);
    }

    private List<SqlJoinClause> ParseJoinClauses(ref TokenLexer lexer)
    {
        var joins = new List<SqlJoinClause>();

        while (!IsAtEnd(ref lexer))
        {
            if (!IsKeyword(ref lexer, "JOIN") && !IsKeyword(ref lexer, "INNER") &&
                !IsKeyword(ref lexer, "LEFT") && !IsKeyword(ref lexer, "RIGHT") &&
                !IsKeyword(ref lexer, "FULL") && !IsKeyword(ref lexer, "CROSS"))
            {
                break;
            }

            int joinStart = lexer.Current.Position;
            var joinLexer = lexer;
            int joinEnd = joinStart + lexer.Current.Value.Length;
            while (!IsAtEnd(ref joinLexer) && !IsKeyword(ref joinLexer, "JOIN"))
            {
                if (!joinLexer.MoveNext())
                {
                    break;
                }
            }
            if (IsKeyword(ref joinLexer, "JOIN"))
            {
                joinEnd = joinLexer.Current.Position + joinLexer.Current.Value.Length;
            }

            SqlJoinType? joinType = TryParseJoinType(ref lexer);
            if (!joinType.HasValue)
            {
                break;
            }

            // Expect table reference after JOIN keyword
            var table = ParseRelationReference(ref lexer);

            // ON condition
            SqlExpression? condition = null;
            bool hasOn = !IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ON");
            if (hasOn)
            {
                Advance(ref lexer);
                if (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon ||
                    IsStatementBoundaryKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, "Expected a predicate after JOIN ... ON.");
                }
                else
                {
                    condition = ParseExpression(ref lexer);
                }
            }

            var usingColumns = new List<string>();
            bool hasUsing = IsKeyword(ref lexer, "USING");
            if (hasUsing)
            {
                Advance(ref lexer);
                if (lexer.Current.Type != TokenType.LeftParen)
                {
                    AddSyntaxDiagnostic(ref lexer, "Expected '(' after USING.");
                }
                else
                {
                    Advance(ref lexer);
                    while (true)
                    {
                        if (!IsIdentifierOrKeyword(ref lexer) || IsStatementBoundaryKeyword(ref lexer))
                        {
                            AddSyntaxDiagnostic(ref lexer, "Expected a column name in USING.");
                            break;
                        }
                        usingColumns.Add(CurrentIdentifierText(ref lexer));
                        Advance(ref lexer);
                        if (lexer.Current.Type != TokenType.Comma) { break; }
                        Advance(ref lexer);
                    }
                    if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
                    else { AddSyntaxDiagnostic(ref lexer, "Expected ')' after USING columns."); }
                }
            }
            if (!SqlLanguageProfile.SupportsJoin(joinType.Value))
            {
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd,
                    "SQL supports INNER, LEFT [OUTER] and CROSS JOIN; RIGHT OUTER JOIN and FULL OUTER JOIN are not supported.");
            }
            else if (joinType == SqlJoinType.Cross && (hasOn || hasUsing))
            {
                AddSyntaxDiagnostic(joinStart, joinEnd, "CROSS JOIN cannot have ON or USING.");
            }
            else if (joinType != SqlJoinType.Cross && !hasOn && !hasUsing)
            {
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd, "INNER and LEFT JOIN require ON or USING.");
            }
            if (hasOn && hasUsing)
            {
                AddSyntaxDiagnostic(joinStart, joinEnd, "JOIN accepts exactly one of ON or USING.");
            }

            joins.Add(new SqlJoinClause(joinType.Value, table, condition, usingColumns));
        }

        return joins;
    }

    private SqlRelationReference ParseRelationReference(ref TokenLexer lexer)
    {
        if (lexer.Current.Type != TokenType.LeftParen)
        {
            return ParseRequiredTableReference(ref lexer);
        }

        Advance(ref lexer);
        if (!IsKeyword(ref lexer, "SELECT") && !IsKeyword(ref lexer, "WITH"))
        {
            AddSyntaxDiagnostic(ref lexer, "Expected SELECT in a derived table.");
            return new SqlTableReference("?", null, null);
        }
        var query = ParseSubquery(ref lexer);
        if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
        else { AddSyntaxDiagnostic(ref lexer, "Expected ')' after the derived query."); }
        if (IsKeyword(ref lexer, "AS")) { Advance(ref lexer); }
        string alias = "?";
        if (IsIdentifierOrKeyword(ref lexer) && !IsStatementBoundaryKeyword(ref lexer))
        {
            alias = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }
        else { AddSyntaxDiagnostic(ref lexer, "A derived table requires an alias."); }

        var columns = new List<string>();
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
            while (true)
            {
                if (!IsIdentifierOrKeyword(ref lexer) || IsStatementBoundaryKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, "Expected a derived column name.");
                    break;
                }
                columns.Add(CurrentIdentifierText(ref lexer));
                Advance(ref lexer);
                if (lexer.Current.Type != TokenType.Comma) { break; }
                Advance(ref lexer);
            }
            if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
            else { AddSyntaxDiagnostic(ref lexer, "Expected ')' after derived column names."); }
        }
        return new SqlDerivedTableReference(query, alias, columns.AsReadOnly());
    }

    private void AddUnsupportedSurfaceDiagnostic(int start, int end, string message)
    {
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = "COHDBL001",
            Message = message,
            Start = start,
            End = end,
            Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute,
        });
    }

    private SqlJoinType? TryParseJoinType(ref TokenLexer lexer)
    {
        if (IsAtEnd(ref lexer))
        {
            return null;
        }

        if (IsKeyword(ref lexer, "JOIN") || IsKeyword(ref lexer, "INNER"))
        {
            if (IsKeyword(ref lexer, "INNER"))
            {
                Advance(ref lexer);
                ConsumeRequiredJoinKeyword(ref lexer);
            }
            else
            {
                Advance(ref lexer); // consume JOIN
            }
            return SqlJoinType.Inner;
        }

        if (IsKeyword(ref lexer, "LEFT"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OUTER"))
            {
                Advance(ref lexer);
            }

            ConsumeRequiredJoinKeyword(ref lexer);

            return SqlJoinType.LeftOuter;
        }

        if (IsKeyword(ref lexer, "RIGHT"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OUTER"))
            {
                Advance(ref lexer);
            }

            ConsumeRequiredJoinKeyword(ref lexer);

            return SqlJoinType.RightOuter;
        }

        if (IsKeyword(ref lexer, "FULL"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OUTER"))
            {
                Advance(ref lexer);
            }

            ConsumeRequiredJoinKeyword(ref lexer);

            return SqlJoinType.FullOuter;
        }

        if (IsKeyword(ref lexer, "CROSS"))
        {
            Advance(ref lexer);
            ConsumeRequiredJoinKeyword(ref lexer);

            return SqlJoinType.Cross;
        }

        return null;
    }

    private void ConsumeRequiredJoinKeyword(ref TokenLexer lexer)
    {
        if (IsKeyword(ref lexer, "JOIN"))
        {
            Advance(ref lexer);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, "Expected JOIN after the join type.");
        }
    }

    private SqlOrderByColumn ParseOrderByColumn(ref TokenLexer lexer)
    {
        var expr = ParseExpression(ref lexer);
        bool isDescending = false;

        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "DESC"))
        {
            isDescending = true;
            Advance(ref lexer);
        }
        else if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ASC"))
        {
            Advance(ref lexer);
        }

        if (!IsAtEnd(ref lexer) && lexer.Current.Type is TokenType.Identifier or TokenType.Keyword &&
            CurrentText(ref lexer).Equals("NULLS", StringComparison.OrdinalIgnoreCase) &&
            TryPeekToken(lexer, out string placement, out int end) &&
            (placement.Equals("FIRST", StringComparison.OrdinalIgnoreCase) ||
             placement.Equals("LAST", StringComparison.OrdinalIgnoreCase)))
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position, end,
                "SQL ORDER BY NULLS FIRST and NULLS LAST are not supported; NULL sorts first in ASC and last in DESC.");
            Advance(ref lexer);
            Advance(ref lexer);
        }

        return new SqlOrderByColumn(expr, isDescending);
    }
}
