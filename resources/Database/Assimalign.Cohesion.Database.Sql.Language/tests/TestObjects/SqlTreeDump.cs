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

    internal static string Dump(SqlSelectExpression select)
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
                builder.Append(')');
                break;
            case SqlCollateExpression collate:
                builder.Append("Collate(").Append(Text(collate.CollationName)).Append(", ");
                Append(builder, collate.Operand);
                builder.Append(')');
                break;
            case SqlFunctionCallExpression function:
                builder.Append("Call(").Append(Text(function.FunctionName)).Append(", ");
                AppendList(builder, function.Arguments);
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

    private static void Append(StringBuilder builder, SqlSelectExpression select)
    {
        builder.Append("Select(distinct=").Append(select.IsDistinct).Append(", columns=[");
        for (int index = 0; index < select.Columns.Count; index++)
        {
            builder.Append(index > 0 ? ", " : string.Empty);
            Append(builder, select.Columns[index].Expression);
            builder.Append(" as ").Append(Text(select.Columns[index].Alias));
        }
        builder.Append("], from=").Append(Table(select.From)).Append(", joins=[");
        foreach (var join in select.Joins)
        {
            builder.Append(join.JoinType).Append(' ').Append(Table(join.Table)).Append(" on ");
            Append(builder, join.Condition);
            builder.Append("; ");
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
            builder.Append(order.IsDescending ? " desc; " : " asc; ");
        }
        builder.Append("], limit=");
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

    private static string Table(SqlTableReference? table)
        => table is null ? "null" : $"Table({Text(table.SchemaName)}, {Text(table.TableName)}, {Text(table.Alias)})";

    // Brackets make leading, trailing and embedded whitespace visible in a failure message.
    private static string Text(string? value) => value is null ? "null" : "[" + value + "]";
}
