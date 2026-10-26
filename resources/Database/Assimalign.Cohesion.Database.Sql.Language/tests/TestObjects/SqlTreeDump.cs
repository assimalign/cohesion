using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Writes a parsed tree as one line of text that names every node and every semantic field —
/// operators, negation flags, literal kinds and values, names, call arguments, CASE branches,
/// CAST targets and query clauses — and nothing else. Source positions and statement text are
/// left out, so two trees are structurally equal exactly when their dumps are equal, and a
/// failed comparison shows where they differ.
/// </summary>
internal static class SqlTreeDump
{
    internal static string Dump(SqlExpression? expression)
    {
        var builder = new StringBuilder();
        Append(builder, expression);
        return builder.ToString();
    }

    internal static string Dump(SqlQueryBodyExpression select)
    {
        var builder = new StringBuilder();
        Append(builder, select);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, SqlExpression? expression)
    {
        switch (expression)
        {
            case null:
                builder.Append("null");
                break;
            case SqlLogicalExpression logical:
                builder.Append("Logical(").Append(logical.Operator).Append(", ");
                AppendList(builder, logical.Operands);
                builder.Append(')');
                break;
            case SqlBinaryExpression binary:
                builder.Append("Binary(").Append(binary.Operator).Append(", ");
                Append(builder, binary.Left);
                builder.Append(", ");
                Append(builder, binary.Right);
                builder.Append(')');
                break;
            case SqlUnaryExpression unary:
                builder.Append("Unary(").Append(unary.Operator).Append(", ");
                Append(builder, unary.Operand);
                builder.Append(')');
                break;
            case SqlLiteralExpression literal:
                builder.Append("Literal(").Append(literal.LiteralType).Append(", ").Append(Text(literal.Value)).Append(')');
                break;
            case SqlDefaultExpression:
                builder.Append("Default");
                break;
            case SqlColumnReferenceExpression column:
                builder.Append("Column(").Append(Text(column.SchemaName)).Append(", ").Append(Text(column.TableAlias))
                       .Append(", ").Append(Text(column.ColumnName)).Append(')');
                break;
            case SqlParameterExpression parameter:
                builder.Append("Parameter(").Append(Text(parameter.ParameterName)).Append(')');
                break;
            case SqlStarExpression:
                builder.Append("Star");
                break;
            case SqlDistinctExpression distinct:
                builder.Append("Distinct(").Append(distinct.IsNegated).Append(", ");
                Append(builder, distinct.Left);
                builder.Append(", ");
                Append(builder, distinct.Right);
                builder.Append(')');
                break;
            case SqlBooleanTestExpression test:
                builder.Append("BooleanTest(").Append(test.TruthValue).Append(", ").Append(test.IsNegated).Append(", ");
                Append(builder, test.Operand);
                builder.Append(')');
                break;
            case SqlIsNullExpression isNull:
                builder.Append("IsNull(").Append(isNull.IsNegated).Append(", ");
                Append(builder, isNull.Operand);
                builder.Append(')');
                break;
            case SqlBetweenExpression between:
                builder.Append("Between(").Append(between.IsNegated).Append(", ");
                Append(builder, between.Operand);
                builder.Append(", ");
                Append(builder, between.Low);
                builder.Append(", ");
                Append(builder, between.High);
                builder.Append(')');
                break;
            case SqlQuantifiedComparisonExpression quantified:
                builder.Append("Quantified(").Append(quantified.Operator).Append(", ").Append(quantified.Quantifier).Append(", ");
                Append(builder, quantified.Operand);
                builder.Append(", ");
                Append(builder, quantified.Subquery);
                builder.Append(')');
                break;
            case SqlInExpression inExpression:
                builder.Append("In(").Append(inExpression.IsNegated).Append(", ");
                Append(builder, inExpression.Operand);
                builder.Append(", ");
                if (inExpression.Subquery is not null)
                {
                    Append(builder, inExpression.Subquery);
                }
                else
                {
                    AppendList(builder, inExpression.Values ?? Array.Empty<SqlExpression>());
                }
                builder.Append(')');
                break;
            case SqlLikeExpression like:
                builder.Append("Like(").Append(like.IsNegated).Append(", ");
                Append(builder, like.Operand);
                builder.Append(", ");
                Append(builder, like.Pattern);
                builder.Append(", ");
                Append(builder, like.Escape);
                builder.Append(')');
                break;
            case SqlCollateExpression collate:
                builder.Append("Collate(").Append(Text(collate.CollationName)).Append(", ");
                Append(builder, collate.Operand);
                builder.Append(')');
                break;
            case SqlFunctionCallExpression function:
                builder.Append("Call(").Append(Text(function.FunctionName)).Append(", ").Append(function.Quantifier).Append(", ").Append(function.HasWithinGroupOrdering).Append(", ");
                AppendList(builder, function.Arguments);
                foreach (var order in function.OrderBy)
                {
                    builder.Append(order.NullPlacement).Append(' ').Append(order.IsDescending ? ", DESC " : ", ASC ");
                    Append(builder, order.Expression);
                }
                builder.Append(')');
                break;
            case SqlCaseExpression caseExpression:
                builder.Append("Case(");
                Append(builder, caseExpression.Input);
                foreach (var clause in caseExpression.WhenClauses)
                {
                    builder.Append(", When(");
                    Append(builder, clause.Condition);
                    builder.Append(", ");
                    Append(builder, clause.Result);
                    builder.Append(')');
                }
                builder.Append(", Else(");
                Append(builder, caseExpression.ElseResult);
                builder.Append("))");
                break;
            case SqlCastExpression cast:
                // The target spelling renders normalized; its identity is the resolved type.
                var info = cast.TargetTypeInfo;
                builder.Append("Cast(")
                       .Append(string.Concat(cast.TargetType.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant())
                       .Append(", ").Append(info?.Type.ToString() ?? "unresolved").Append('/').Append(info?.MaxLength)
                       .Append('/').Append(info?.Precision).Append('/').Append(info?.Scale).Append(", ");
                Append(builder, cast.Operand);
                builder.Append(')');
                break;
            case SqlExistsExpression exists:
                builder.Append("Exists(").Append(exists.IsNegated).Append(", ");
                Append(builder, exists.Subquery);
                builder.Append(')');
                break;
            case SqlSubqueryExpression subquery:
                builder.Append("Subquery(");
                Append(builder, subquery.Select);
                builder.Append(')');
                break;
            default:
                throw new NotSupportedException($"No dump for {expression.GetType().Name}.");
        }
    }

    private static void Append(StringBuilder builder, SqlQueryBodyExpression query)
    {
        if (query is SqlSetOperationExpression set)
        {
            builder.Append("Set(").Append(set.Operator).Append(", all=").Append(set.IsAll).Append(", with=[");
            foreach (var definition in set.CommonTables)
            {
                builder.Append(Text(definition.Name)).Append('[').Append(string.Join(",", definition.Columns.Select(Text))).Append("]=");
                Append(builder, definition.Body);
            }
            builder.Append("], left=");
            Append(builder, set.Left);
            builder.Append(", right=");
            Append(builder, set.Right);
            builder.Append(", order=[");
            foreach (var order in set.OrderBy)
            {
                Append(builder, order.Expression);
                builder.Append(order.NullPlacement).Append(' ').Append(order.IsDescending ? " desc;" : " asc;");
            }
            builder.Append("], limitSyntax=").Append(set.LimitSyntaxKind).Append(", limit=");
            Append(builder, set.Limit);
            builder.Append(", offset=");
            Append(builder, set.Offset);
            builder.Append(')');
            return;
        }
        var select = (SqlSelectExpression)query;
        builder.Append("Select(commonTables=[");
        for (int index = 0; index < select.CommonTables.Count; index++)
        {
            var definition = select.CommonTables[index];
            builder.Append(index > 0 ? ", " : string.Empty);
            builder.Append("CommonTable(").Append(Text(definition.Name)).Append(", columns=[")
                .Append(string.Join(", ", definition.Columns.Select(Text))).Append("], body=");
            Append(builder, definition.Body);
            builder.Append(')');
        }
        builder.Append("], distinct=").Append(select.IsDistinct).Append(", columns=[");
        for (int index = 0; index < select.Columns.Count; index++)
        {
            builder.Append(index > 0 ? ", " : string.Empty);
            Append(builder, select.Columns[index].Expression);
            builder.Append(" as ").Append(Text(select.Columns[index].Alias));
        }
        builder.Append("], from=[");
        foreach (var item in select.From)
        {
            builder.Append(Table(item.Table)).Append(", joins=[");
            foreach (var join in item.Joins)
            {
                builder.Append(join.JoinType).Append(' ').Append(Table(join.Table)).Append(" using ").Append(string.Join(",", join.UsingColumns)).Append(" on ");
                Append(builder, join.Condition);
                builder.Append("; ");
            }
            builder.Append("]; ");
        }
        builder.Append("], where=");
        Append(builder, select.Where);
        builder.Append(", groupBy=");
        AppendList(builder, select.GroupBy);
        builder.Append(", having=");
        Append(builder, select.Having);
        builder.Append(", orderBy=[");
        foreach (var order in select.OrderBy)
        {
            Append(builder, order.Expression);
            builder.Append(order.NullPlacement).Append(' ').Append(order.IsDescending ? " desc; " : " asc; ");
        }
        builder.Append("], limitSyntax=").Append(select.LimitSyntaxKind).Append(", limit=");
        Append(builder, select.Limit);
        builder.Append(", offset=");
        Append(builder, select.Offset);
        builder.Append(')');
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<SqlExpression> expressions)
    {
        builder.Append('[');
        for (int index = 0; index < expressions.Count; index++)
        {
            builder.Append(index > 0 ? ", " : string.Empty);
            Append(builder, expressions[index]);
        }
        builder.Append(']');
    }

    private static string Table(SqlRelationReference? relation)
    {
        if (relation is SqlJoinedTableReference group)
        {
            return "Group(" + Table(group.Item.Table) + "; " + string.Join("; ", group.Item.Joins.Select(join =>
                join.JoinType + " " + Table(join.Table) + " USING " + string.Join(",", join.UsingColumns) + " ON " + Dump(join.Condition))) + ")";
        }
        if (relation is SqlDerivedTableReference derived)
        {
            var builder = new StringBuilder("Derived(");
            Append(builder, derived.Query);
            return builder.Append(", ").Append(Text(derived.Alias)).Append(", [")
                .Append(string.Join(", ", derived.Columns.Select(Text))).Append("])").ToString();
        }
        return relation is SqlTableReference table
            ? $"Table({Text(table.SchemaName)}, {Text(table.TableName)}, {Text(table.Alias)})" : "null";
    }

    // Brackets make leading, trailing and embedded whitespace visible in a failure message.
    private static string Text(string? value) => value is null ? "null" : "[" + value + "]";
}
