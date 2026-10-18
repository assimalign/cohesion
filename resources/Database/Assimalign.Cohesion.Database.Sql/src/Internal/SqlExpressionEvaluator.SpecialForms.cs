using System;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    internal void ValidateEvaluatorForm(SqlFunctionCallExpression call)
    {
        CheckEvaluatorForm(call);
        if (call.FunctionName.Equals("EXTRACT", StringComparison.OrdinalIgnoreCase))
        {
            if (call.Arguments[0] is not SqlLiteralExpression { LiteralType: SqlLiteralType.String } field ||
                !SqlTemporalFunctions.AcceptsField(field.Value, StaticTypeOf(call.Arguments[1])))
            {
                throw SqlEvaluationException.FunctionArgumentTypeMismatch(call.FunctionName,
                    SqlType.NameOf(StaticTypeOf(call.Arguments[1])), "EXTRACT(field FROM compatible temporal value)");
            }
        }
        else if (call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase))
        {
            var left = StaticTypeOf(call.Arguments[0]);
            var right = StaticTypeOf(call.Arguments[1]);
            if (left != DatabaseType.Null && right != DatabaseType.Null && left != right &&
                !(SqlFunctionResolver.IsNumeric(left) && SqlFunctionResolver.IsNumeric(right)) &&
                !(SqlTemporalArithmetic.IsCalendar(left) && SqlTemporalArithmetic.IsCalendar(right)) &&
                !(left is DatabaseType.String or DatabaseType.Character && right is DatabaseType.String or DatabaseType.Character))
            {
                throw SqlEvaluationException.FunctionArgumentTypeMismatch(call.FunctionName,
                    SqlType.NameOf(left) + ", " + SqlType.NameOf(right), "NULLIF(comparable value, comparable value)");
            }
        }
    }

    private object? EvaluateSpecialForm(SqlBoundSpecialForm form, object?[] row, bool copySharedBinary)
    {
        object? first = EvaluateCore(form.Arguments[0], row);
        object? second = EvaluateCore(form.Arguments[1], row);
        if (form.Name == "EXTRACT")
        {
            return first is null || second is null ? null : SqlTemporalFunctions.Extract((string)first, second);
        }
        return first is not null && second is not null && SqlValueComparer.Compare(first, second, form.Collation.Resolve(_subqueryValues)) == 0
            ? null : Deliver(first, copySharedBinary);
    }
}
