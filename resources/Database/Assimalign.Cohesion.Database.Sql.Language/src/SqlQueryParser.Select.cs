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
        IReadOnlyList<SqlFromItem> from = [];
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "FROM"))
        {
            Advance(ref lexer);
            from = ParseFromItems(ref lexer);
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
        bool groupByDistinct = false;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "GROUP"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "BY"))
            {
                Advance(ref lexer);
            }
            else
            {
                AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseSelectVoid);
            }

            if (IsKeyword(ref lexer, "DISTINCT") || IsKeyword(ref lexer, "ALL"))
            {
                groupByDistinct = IsKeyword(ref lexer, "DISTINCT");
                Advance(ref lexer);
            }
            while (true)
            {
                if (IsAtEnd(ref lexer) || lexer.Current.Type is TokenType.Semicolon or TokenType.RightParen or TokenType.Comma ||
                    IsStatementBoundaryKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseSelectVoid2);
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
                AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseSelectVoid3);
            }
            else
            {
                having = ParseExpression(ref lexer);
            }
        }

        // Consume trailing semicolon (don't advance past it so ParseCore picks it up)
        // The semicolon is tracked by TrackToken when we advance

        return new SqlSelectExpression(
            columns, from, where, groupBy, having, [],
            limit, null, isDistinct, null,
            Location.Create(1, 1, pos, _lastTokenEnd), limitSyntaxKind, groupByDistinct);
    }

    private IReadOnlyList<SqlFromItem> ParseFromItems(ref TokenLexer lexer)
    {
        var items = new List<SqlFromItem>();
        do
        {
            var relation = ParseRelationReference(ref lexer);
            items.Add(new SqlFromItem(relation, ParseJoinClauses(ref lexer)));
            if (IsAtEnd(ref lexer) || lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        } while (true);
        return items;
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
        AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseTopCountVoid);
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
                 !TryReadSelectLockingStrength(lexer, out _) &&
                 !IsStatementBoundaryKeyword(ref lexer))
        {
            // Check it's not a keyword that starts the next clause
            alias = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }

        return new SqlSelectColumn(expr, alias);
    }

    private List<SqlJoinClause> ParseJoinClauses(ref TokenLexer lexer, bool mergeSource = false)
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
            // A top-level source CROSS JOIN has no condition; the next ON belongs to MERGE.
            bool hasOn = !(mergeSource && joinType == SqlJoinType.Cross) &&
                !IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ON");
            if (hasOn)
            {
                Advance(ref lexer);
                if (IsAtEnd(ref lexer) || lexer.Current.Type == TokenType.Semicolon ||
                    IsStatementBoundaryKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseJoinClausesVoid);
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
                    AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseJoinClausesVoid2);
                }
                else
                {
                    Advance(ref lexer);
                    while (true)
                    {
                        if (!IsIdentifierOrKeyword(ref lexer) || IsStatementBoundaryKeyword(ref lexer))
                        {
                            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseJoinClausesVoid3);
                            break;
                        }
                        usingColumns.Add(CurrentIdentifierText(ref lexer));
                        Advance(ref lexer);
                        if (lexer.Current.Type != TokenType.Comma) { break; }
                        Advance(ref lexer);
                    }
                    if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
                    else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseJoinClausesVoid4); }
                }
            }
            if (!SqlLanguageProfile.SupportsJoin(joinType.Value))
            {
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd,
                    ErrorMessages.SqlQueryParserParseJoinClausesVoid5);
            }
            else if (joinType == SqlJoinType.Cross && (hasOn || hasUsing))
            {
                AddSyntaxDiagnostic(joinStart, joinEnd, ErrorMessages.SqlQueryParserParseJoinClausesVoid6);
            }
            else if (joinType != SqlJoinType.Cross && !hasOn && !hasUsing)
            {
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd, ErrorMessages.SqlQueryParserParseJoinClausesVoid7);
            }
            if (hasOn && hasUsing)
            {
                AddSyntaxDiagnostic(joinStart, joinEnd, ErrorMessages.SqlQueryParserParseJoinClausesVoid8);
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

        int groupStart = lexer.Current.Position;
        Advance(ref lexer);
        SqlQueryBodyExpression query;
        // A leading parenthesized SELECT can be the first relation of a joined group.
        // Check its closing continuation, including the derived alias (PostgreSQL gram.y:13938-14022).
        if (!StartsQuery(lexer))
        {
            if (!TryEnterParenthesis(ref lexer, groupStart)) { return new SqlTableReference("?", null, null); }
            SqlFromItem item;
            try
            {
                var left = ParseRelationReference(ref lexer);
                item = new SqlFromItem(left, ParseJoinClauses(ref lexer));
            }
            finally { _parenthesisDepth--; }
            if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
            else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.QueryParserParseRelationReferenceVoid2); }
            if (!IsKeyword(ref lexer, "AS") && (!IsIdentifierOrKeyword(ref lexer) || IsStatementBoundaryKeyword(ref lexer)))
            {
                return new SqlJoinedTableReference(item);
            }
            // A group alias hides its constituent namespaces just like a derived SELECT *.
            query = new SqlSelectExpression([new SqlSelectColumn(new SqlStarExpression(null), null)], [item],
                null, [], null, [], null, null, false, null, Location.Create(1, 1, groupStart, _lastTokenEnd));
        }
        else
        {
            query = ParseSubquery(ref lexer);
            if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
            else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.QueryParserParseRelationReferenceVoid2); }
        }
        if (IsKeyword(ref lexer, "AS")) { Advance(ref lexer); }
        string alias = "?";
        if (IsIdentifierOrKeyword(ref lexer) && !IsStatementBoundaryKeyword(ref lexer))
        {
            alias = CurrentIdentifierText(ref lexer);
            Advance(ref lexer);
        }
        else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.QueryParserParseRelationReferenceVoid3); }

        var columns = new List<string>();
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
            while (true)
            {
                if (!IsIdentifierOrKeyword(ref lexer) || IsStatementBoundaryKeyword(ref lexer))
                {
                    AddSyntaxDiagnostic(ref lexer, ErrorMessages.QueryParserParseRelationReferenceVoid4);
                    break;
                }
                columns.Add(CurrentIdentifierText(ref lexer));
                Advance(ref lexer);
                if (lexer.Current.Type != TokenType.Comma) { break; }
                Advance(ref lexer);
            }
            if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
            else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.QueryParserParseRelationReferenceVoid5); }
        }
        return new SqlDerivedTableReference(query, alias, columns.AsReadOnly());
    }

    private void AddUnsupportedSurfaceDiagnostic(int start, int end, string message)
    {
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = "CK00001",
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
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.ParserConsumeRequiredJoinKeywordVoid);
        }
    }

    private SqlOrderByColumn ParseOrderByColumn(ref TokenLexer lexer, bool aggregateInput = false)
    {
        var expr = aggregateInput ? ParseOperand(ref lexer, OperandRule.Expression) : ParseExpression(ref lexer);
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

        var nullPlacement = SqlNullPlacement.Default;
        if (!IsAtEnd(ref lexer) && lexer.Current.Type is TokenType.Identifier or TokenType.Keyword &&
            CurrentText(ref lexer).Equals("NULLS", StringComparison.OrdinalIgnoreCase) &&
            TryPeekToken(lexer, out string placement, out _) &&
            (placement.Equals("FIRST", StringComparison.OrdinalIgnoreCase) ||
             placement.Equals("LAST", StringComparison.OrdinalIgnoreCase)))
        {
            nullPlacement = placement.Equals("FIRST", StringComparison.OrdinalIgnoreCase) ? SqlNullPlacement.First : SqlNullPlacement.Last;
            Advance(ref lexer);
            Advance(ref lexer);
        }

        return new SqlOrderByColumn(expr, isDescending, nullPlacement);
    }
}
