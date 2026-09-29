using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    private const int MaximumSubqueryDepth = 32;
    private int _subqueryDepth;
    private int _paginationDepth;

    private SqlSelectExpression ParseSubquery(ref TokenLexer lexer)
    {
        if (_paginationDepth > 0)
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length,
                "SQL subqueries in LIMIT or OFFSET expressions are not supported.");
        }

        if (_subqueryDepth >= MaximumSubqueryDepth)
        {
            AddUnsupportedSurfaceDiagnostic(lexer.Current.Position,
                lexer.Current.Position + lexer.Current.Value.Length,
                $"SQL subquery nesting exceeds the supported limit of {MaximumSubqueryDepth} levels.");

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

            return new SqlSelectExpression([], null, [], null, [], null, [],
                null, null, false, null, null);
        }

        _subqueryDepth++;
        try
        {
            var select = ParseSelect(ref lexer);
            ValidateSubqueryReferences(select);
            return select;
        }
        finally
        {
            _subqueryDepth--;
        }
    }

    private SqlExpression ParsePaginationExpression(ref TokenLexer lexer)
    {
        _paginationDepth++;
        try
        {
            return ParseExpression(ref lexer);
        }
        finally
        {
            _paginationDepth--;
        }
    }

    private static bool TryFindUnsupportedSubqueryForm(
        TokenLexer lexer,
        string firstToken,
        string? previousToken,
        string? previousPreviousToken,
        bool insertValues,
        out string clause)
    {
        clause = string.Empty;
        if (lexer.Current.Type is not (TokenType.Identifier or TokenType.Keyword))
        {
            return false;
        }

        string token = CurrentText(ref lexer);
        if ((token.Equals("ANY", StringComparison.OrdinalIgnoreCase) ||
             token.Equals("ALL", StringComparison.OrdinalIgnoreCase) ||
             token.Equals("SOME", StringComparison.OrdinalIgnoreCase)) &&
            previousToken is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=" &&
            TryPeekToken(lexer, out string next, out _) && next == "(")
        {
            clause = $"{token.ToUpperInvariant()} quantified comparison";
        }
        else if (token.Equals("LATERAL", StringComparison.OrdinalIgnoreCase) &&
                 (string.Equals(previousToken, "FROM", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(previousToken, "JOIN", StringComparison.OrdinalIgnoreCase)))
        {
            clause = "LATERAL subquery";
        }
        else if (lexer.Current.Type == TokenType.Keyword &&
                 token.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && previousToken is not null)
        {
            if (previousToken == "(" &&
                (string.Equals(previousPreviousToken, "FROM", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(previousPreviousToken, "JOIN", StringComparison.OrdinalIgnoreCase)))
            {
                clause = "derived-table SUBQUERY in FROM or JOIN";
            }
            else if (!firstToken.Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
                     !firstToken.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
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

    private void ValidateSubqueryReferences(SqlSelectExpression select)
    {
        var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSubqueryQualifier(select.From, qualifiers);
        foreach (var join in select.Joins)
        {
            AddSubqueryQualifier(join.Table, qualifiers);
        }

        foreach (var column in select.Columns)
        {
            ValidateSubqueryReference(column.Expression, qualifiers);
        }
        foreach (var join in select.Joins)
        {
            ValidateSubqueryReference(join.Condition, qualifiers);
        }
        ValidateSubqueryReference(select.Where, qualifiers);
        foreach (var group in select.GroupBy)
        {
            ValidateSubqueryReference(group, qualifiers);
        }
        ValidateSubqueryReference(select.Having, qualifiers);
        foreach (var order in select.OrderBy)
        {
            ValidateSubqueryReference(order.Expression, qualifiers);
        }
        ValidateSubqueryReference(select.Limit, qualifiers);
        ValidateSubqueryReference(select.Offset, qualifiers);
    }

    private static void AddSubqueryQualifier(SqlTableReference? table, HashSet<string> qualifiers)
    {
        if (table is null)
        {
            return;
        }
        qualifiers.Add(table.Alias ?? table.TableName);
        if (table.Alias is null)
        {
            // An omitted schema is resolved by the catalog. Its eventual fully
            // qualified column reference must not be mistaken for correlation.
            qualifiers.Add($"{table.SchemaName ?? "*"}.{table.TableName}");
        }
    }

    private void ValidateSubqueryReference(SqlExpression? expression, HashSet<string> qualifiers)
    {
        switch (expression)
        {
            case SqlColumnReferenceExpression { TableAlias: not null } column:
                string qualifier = column.SchemaName is null
                    ? column.TableAlias : $"{column.SchemaName}.{column.TableAlias}";
                if (!qualifiers.Contains(qualifier) &&
                    !(column.SchemaName is not null && qualifiers.Contains($"*.{column.TableAlias}")))
                {
                    AddUnsupportedSurfaceDiagnostic(column.Location?.Start ?? 0, column.Location?.End ?? 0,
                        $"Correlated SQL subqueries are not supported: reference '{qualifier}.{column.ColumnName}' is outside the subquery's local FROM/JOIN scope.");
                }
                break;
            case SqlBinaryExpression binary:
                ValidateSubqueryReference(binary.Left, qualifiers);
                ValidateSubqueryReference(binary.Right, qualifiers);
                break;
            case SqlUnaryExpression unary:
                ValidateSubqueryReference(unary.Operand, qualifiers);
                break;
            case SqlBetweenExpression between:
                ValidateSubqueryReference(between.Operand, qualifiers);
                ValidateSubqueryReference(between.Low, qualifiers);
                ValidateSubqueryReference(between.High, qualifiers);
                break;
            case SqlInExpression membership:
                ValidateSubqueryReference(membership.Operand, qualifiers);
                if (membership.Values is not null)
                {
                    foreach (var value in membership.Values)
                    {
                        ValidateSubqueryReference(value, qualifiers);
                    }
                }
                break;
            case SqlLikeExpression like:
                ValidateSubqueryReference(like.Operand, qualifiers);
                ValidateSubqueryReference(like.Pattern, qualifiers);
                break;
            case SqlIsNullExpression isNull:
                ValidateSubqueryReference(isNull.Operand, qualifiers);
                break;
            case SqlCaseExpression choice:
                ValidateSubqueryReference(choice.Input, qualifiers);
                foreach (var when in choice.WhenClauses)
                {
                    ValidateSubqueryReference(when.Condition, qualifiers);
                    ValidateSubqueryReference(when.Result, qualifiers);
                }
                ValidateSubqueryReference(choice.ElseResult, qualifiers);
                break;
            case SqlCastExpression cast:
                ValidateSubqueryReference(cast.Operand, qualifiers);
                break;
            case SqlCollateExpression collate:
                ValidateSubqueryReference(collate.Operand, qualifiers);
                break;
            case SqlFunctionCallExpression function:
                foreach (var argument in function.Arguments)
                {
                    ValidateSubqueryReference(argument, qualifiers);
                }
                break;
            // Nested query bodies have already been checked in their own scope.
        }
    }
}
