using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language.Sql;

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

            groupBy.Add(ParseExpression(ref lexer));
            while (!IsAtEnd(ref lexer) && lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                groupBy.Add(ParseExpression(ref lexer));
            }
        }

        // HAVING
        SqlExpression? having = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "HAVING"))
        {
            Advance(ref lexer);
            having = ParseExpression(ref lexer);
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
            limit = ParseExpression(ref lexer);
        }

        // OFFSET
        SqlExpression? offset = null;
        if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OFFSET"))
        {
            Advance(ref lexer);
            offset = ParseExpression(ref lexer);
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
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "ON"))
            {
                Advance(ref lexer);
                condition = ParseExpression(ref lexer);
            }

            joins.Add(new SqlJoinClause(joinType.Value, table, condition));
        }

        return joins;
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
                if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "JOIN"))
                {
                    Advance(ref lexer);
                }
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

            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "JOIN"))
            {
                Advance(ref lexer);
            }

            return SqlJoinType.LeftOuter;
        }

        if (IsKeyword(ref lexer, "RIGHT"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OUTER"))
            {
                Advance(ref lexer);
            }

            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "JOIN"))
            {
                Advance(ref lexer);
            }

            return SqlJoinType.RightOuter;
        }

        if (IsKeyword(ref lexer, "FULL"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "OUTER"))
            {
                Advance(ref lexer);
            }

            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "JOIN"))
            {
                Advance(ref lexer);
            }

            return SqlJoinType.FullOuter;
        }

        if (IsKeyword(ref lexer, "CROSS"))
        {
            Advance(ref lexer);
            if (!IsAtEnd(ref lexer) && IsKeyword(ref lexer, "JOIN"))
            {
                Advance(ref lexer);
            }

            return SqlJoinType.Cross;
        }

        return null;
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
