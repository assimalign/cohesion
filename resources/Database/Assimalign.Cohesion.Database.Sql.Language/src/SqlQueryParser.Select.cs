using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

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

        // Parse SELECT column list
        var columns = ParseSelectColumns(ref lexer);

        // FROM
        SqlTableReference? from = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "FROM"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                from = ParseTableReference(ref lexer);
            }
        }

        // JOINs
        var joins = ParseJoinClauses(ref lexer);
        if (joins.Count > 0)
        {
            bool joinsSystemRelation = IsSystemJoinReference(from);
            foreach (var join in joins)
            {
                joinsSystemRelation |= IsSystemJoinReference(join.Table);
            }
            if (joinsSystemRelation)
            {
                AddUnsupportedSurfaceDiagnostic(pos, _lastTokenEnd,
                    "The SQL JOIN surface supports stored tables only; joins with INFORMATION_SCHEMA or COHESION_SCHEMA relations are not supported.");
            }
        }
        if (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position, lexer.Current.Position + 1,
                "Comma-separated SQL FROM tables are not supported; use a two-table INNER JOIN with an ON predicate.");
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
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "BY"))
            {
                Advance(ref lexer);
            }

            orderBy.Add(ParseOrderByColumn(ref lexer));
            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                orderBy.Add(ParseOrderByColumn(ref lexer));
            }
        }

        // LIMIT
        SqlExpression? limit = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "LIMIT"))
        {
            Advance(ref lexer);
            limit = ParsePaginationExpression(ref lexer);
        }

        // OFFSET
        SqlExpression? offset = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OFFSET"))
        {
            Advance(ref lexer);
            offset = ParsePaginationExpression(ref lexer);
        }

        // Consume trailing semicolon (don't advance past it so ParseCore picks it up)
        // The semicolon is tracked by TrackToken when we advance

        return new SqlSelectExpression(
            columns, from, joins, where, groupBy, having, orderBy,
            limit, offset, isDistinct, null,
            Location.Create(1, 1, pos, _lastTokenEnd));
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
                alias = CurrentText(ref lexer);
                Advance(ref lexer);
            }
        }
        else if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer) &&
                 !IsStatementBoundaryKeyword(ref lexer))
        {
            // Check it's not a keyword that starts the next clause
            alias = CurrentText(ref lexer);
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
            SqlTableReference? table = null;
            if (!IsAtEnd(ref lexer) && IsIdentifierOrKeyword(ref lexer))
            {
                table = ParseTableReference(ref lexer);
            }
            table ??= new SqlTableReference("?", null, null);

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

            if (joins.Count > 0)
            {
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd,
                    "The SQL JOIN surface supports exactly two tables; additional JOIN clauses are not supported.");
            }
            else if (!SqlLanguageProfile.SupportsJoin(joinType.Value))
            {
                string form = joinType.Value switch
                {
                    SqlJoinType.LeftOuter => "LEFT OUTER JOIN",
                    SqlJoinType.RightOuter => "RIGHT OUTER JOIN",
                    SqlJoinType.FullOuter => "FULL OUTER JOIN",
                    _ => "CROSS JOIN",
                };
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd,
                    $"The {form} form is not supported by the SQL surface; only two-table INNER JOIN with an ON predicate executes.");
            }
            else if (!hasOn && !IsKeyword(ref lexer, "USING"))
            {
                AddUnsupportedSurfaceDiagnostic(joinStart, joinEnd,
                    "The SQL JOIN surface requires an ON predicate; only two-table INNER JOIN with ON executes.");
            }

            joins.Add(new SqlJoinClause(joinType.Value, table, condition));
        }

        return joins;
    }

    private static bool IsSystemJoinReference(SqlTableReference? table) =>
        string.Equals(table?.SchemaName, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(table?.SchemaName, "COHESION_SCHEMA", StringComparison.OrdinalIgnoreCase);

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

        return new SqlOrderByColumn(expr, isDescending);
    }
}
