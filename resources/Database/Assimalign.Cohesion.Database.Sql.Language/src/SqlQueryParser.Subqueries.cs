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

        // The subquery node (scalar, IN or EXISTS) encloses the query's clauses, so they parse
        // one level deeper; the expression limit spans the whole statement (#1151).
        if (!TryEnterOperand(ref lexer))
        {
            return new SqlSelectExpression([], null, [], null, [], null, [],
                null, null, false, null, null);
        }

        _subqueryDepth++;
        try
        {
            var select = ParseSelect(ref lexer);

            // An abandoned statement keeps none of its tree, so there is nothing to check.
            if (!NestingExceeded)
            {
                ValidateSubqueryReferences(select);
            }

            return select;
        }
        finally
        {
            _subqueryDepth--;
            _expressionDepth--;
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

    // Iterative, so its stack use never depends on the tree's depth: a configured limit allows
    // trees thousands of levels deep, and a left-associative chain that deep is built in a loop
    // without the parser recursing (#1151). Operands pop in source order, so the diagnostics
    // come out in the order the references appear.
    private void ValidateSubqueryReference(SqlExpression? root, HashSet<string> qualifiers)
    {
        if (root is null)
        {
            return;
        }

        var pending = new Stack<SqlExpression>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var expression = pending.Pop();
            if (expression is SqlColumnReferenceExpression { TableAlias: not null } column)
            {
                string qualifier = column.SchemaName is null
                    ? column.TableAlias : $"{column.SchemaName}.{column.TableAlias}";
                if (!qualifiers.Contains(qualifier) &&
                    !(column.SchemaName is not null && qualifiers.Contains($"*.{column.TableAlias}")))
                {
                    AddUnsupportedSurfaceDiagnostic(column.Location?.Start ?? 0, column.Location?.End ?? 0,
                        $"Correlated SQL subqueries are not supported: reference '{qualifier}.{column.ColumnName}' is outside the subquery's local FROM/JOIN scope.");
                }

                continue;
            }

            PushOperands(pending, expression);
        }
    }

    /// <summary>
    /// Pushes the operands of a node in reverse source order, so they pop in source order. A
    /// nested query body is not an operand: it has already been checked in its own scope.
    /// </summary>
    private static void PushOperands(Stack<SqlExpression> pending, SqlExpression expression)
    {
        switch (expression)
        {
            case SqlLogicalExpression logical:
                PushReversed(pending, logical.Operands);
                break;
            case SqlBinaryExpression binary:
                pending.Push(binary.Right);
                pending.Push(binary.Left);
                break;
            case SqlUnaryExpression unary:
                pending.Push(unary.Operand);
                break;
            case SqlBetweenExpression between:
                pending.Push(between.High);
                pending.Push(between.Low);
                pending.Push(between.Operand);
                break;
            case SqlInExpression membership:
                if (membership.Values is not null)
                {
                    PushReversed(pending, membership.Values);
                }
                pending.Push(membership.Operand);
                break;
            case SqlLikeExpression like:
                pending.Push(like.Pattern);
                pending.Push(like.Operand);
                break;
            case SqlIsNullExpression isNull:
                pending.Push(isNull.Operand);
                break;
            case SqlCaseExpression choice:
                if (choice.ElseResult is not null)
                {
                    pending.Push(choice.ElseResult);
                }
                for (int index = choice.WhenClauses.Count - 1; index >= 0; index--)
                {
                    pending.Push(choice.WhenClauses[index].Result);
                    pending.Push(choice.WhenClauses[index].Condition);
                }
                if (choice.Input is not null)
                {
                    pending.Push(choice.Input);
                }
                break;
            case SqlCastExpression cast:
                pending.Push(cast.Operand);
                break;
            case SqlCollateExpression collate:
                pending.Push(collate.Operand);
                break;
            case SqlFunctionCallExpression function:
                PushReversed(pending, function.Arguments);
                break;
            // Nested query bodies have already been checked in their own scope.
        }
    }

    private static void PushReversed(Stack<SqlExpression> pending, IReadOnlyList<SqlExpression> operands)
    {
        for (int index = operands.Count - 1; index >= 0; index--)
        {
            pending.Push(operands[index]);
        }
    }
}
