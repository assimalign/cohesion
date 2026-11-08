using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    // One common result type at each CASE/COALESCE boundary, including skipped
    // alternatives. CASE considers ELSE first, as PostgreSQL does.
    // PostgreSQL parse_expr.c:1742-1763,2244-2255; parse_coerce.c:1357-1456.
    private DatabaseType AlternativeResultType(SqlExpression expression, bool alternative = false, Dictionary<SqlExpression, DatabaseType>? types = null, bool jsonPosition = false)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlCollateExpression collate) { return AlternativeResultType(collate.Operand, alternative, types, jsonPosition); }
        if (alternative && expression is SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Null }) { return DatabaseType.Null; }
        if (types is not null && types.TryGetValue(expression, out var known)) { return known; }
        var type = AlternativeResultTypeCore(expression, types, jsonPosition);
        types?.Add(expression, type);
        return type;
    }

    private DatabaseType AlternativeResultTypeCore(SqlExpression expression, Dictionary<SqlExpression, DatabaseType>? types, bool jsonPosition)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            // Resolve an alternative's operator before combining result categories.
            // An invalid bitwise operand retains its own CS02077 diagnostic.
            case SqlUnaryExpression { Operator: SqlUnaryOperator.BitwiseNot } bitwiseNot:
                return ValidateBitwiseOperand(bitwiseNot.Operand, "~");
            case SqlBinaryExpression bitwise when SqlBitwise.IsBinary(bitwise.Operator):
                return SqlBitwise.ResultType(bitwise.Operator, ValidateBitwiseOperand(bitwise.Left, SqlBitwise.Text(bitwise.Operator)),
                    ValidateBitwiseOperand(bitwise.Right, SqlBitwise.Text(bitwise.Operator), SqlBitwise.IsShift(bitwise.Operator)));
            case SqlCaseExpression conditional:
                var type = conditional.ElseResult is null ? DatabaseType.Null : AlternativeResultType(conditional.ElseResult, alternative: true, types, jsonPosition);
                foreach (var clause in conditional.WhenClauses) { type = Common(type, AlternativeResultType(clause.Result, alternative: true, types, jsonPosition), clause.Result); }
                return type == DatabaseType.Null ? DatabaseType.String : type;
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName):
                var common = DatabaseType.Null;
                foreach (var argument in call.Arguments) { common = Common(common, AlternativeResultType(argument, alternative: true, types, jsonPosition), argument); }
                return common == DatabaseType.Null ? DatabaseType.String : common;
            case SqlFunctionCallExpression call when call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count > 0:
                return AlternativeResultType(call.Arguments[0], types: types, jsonPosition: jsonPosition);
            case SqlParameterExpression parameter when jsonPosition && TryGetParameterValue(parameter, out var value)
                && value is not null && ValueTypeOf(value) == DatabaseType.Null:
                throw SqlJsonAccess.Signature(expression.Location);
        }
        return TrySignedIntegerLiteral(expression, out _) ? DatabaseType.Int32 : StaticTypeOf(expression);

        static DatabaseType Common(DatabaseType first, DatabaseType next, SqlExpression syntax)
        {
            if (first == DatabaseType.Null) { return next; }
            if (next == DatabaseType.Null || first == next) { return first; }
            if (first is DatabaseType.String or DatabaseType.Character && next is DatabaseType.String or DatabaseType.Character
                || SqlTemporalArithmetic.IsCalendar(first) && SqlTemporalArithmetic.IsCalendar(next)) { return AlternativeType(first, next); }
            if (SqlFunctionResolver.IsNumeric(first) && SqlFunctionResolver.IsNumeric(next)) { return StatisticsAlternative(first, next); }
            // JSON and JSONB have no implicit conversion, even though both are user types.
            throw new SqlPlanningException("CS02080", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.AlternativesMismatch, SqlType.NameOf(first), SqlType.NameOf(next)), syntax.Location);
        }
    }

    // Group output keeps its established normalization and overload typing. Only a
    // compatible legacy output family is retained; the shared resolver has already
    // checked every declared alternative. Newly admitted unknown inputs use its type.
    private DatabaseType GroupedAlternativeBindingType(SqlExpression expression, DatabaseType common, bool jsonPosition = false)
    {
        if (jsonPosition || _typing?.SlotScope is null) { return common; }
        DatabaseType previous = DatabaseType.Null;
        if (expression is SqlCaseExpression conditional)
        {
            foreach (var clause in conditional.WhenClauses) { previous = AlternativeType(previous, StaticTypeOf(clause.Result)); }
            if (conditional.ElseResult is not null) { previous = AlternativeType(previous, StaticTypeOf(conditional.ElseResult)); }
        }
        else if (expression is SqlFunctionCallExpression call)
        {
            foreach (var argument in call.Arguments) { previous = AlternativeType(previous, StaticTypeOf(argument)); }
        }
        return previous == DatabaseType.Null || previous == common
            || SqlFunctionResolver.IsNumeric(previous) && SqlFunctionResolver.IsNumeric(common)
            || previous == DatabaseType.String && common == DatabaseType.Character ? previous : common;
    }

    private SqlBoundExpression BindAlternativeOperand(SqlExpression expression, out SqlCollationCandidate collation, bool statisticsInput, Dictionary<SqlExpression, DatabaseType> types, bool jsonPosition = false)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (_valueOrdinals is not null && _valueOrdinals.ContainsKey(expression))
        { return Bind(expression, out collation, statisticsInput: statisticsInput); }
        switch (expression)
        {
            case SqlCaseExpression conditional: return BindCase(conditional, out collation, false, statisticsInput, types, jsonPosition);
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName)
                || call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase):
                return BindCall(call, out collation, false, statisticsInput, types, jsonPosition);
            case SqlCollateExpression collate:
                var operand = BindAlternativeOperand(collate.Operand, out var candidate, statisticsInput, types, jsonPosition);
                collation = CollationOf(collate, [candidate]);
                return operand;
        }
        if (TrySignedIntegerLiteral(expression, out int index)) { collation = default; return new SqlBoundConstant(index); }
        return Bind(expression, out collation, statisticsInput: statisticsInput);
    }

    // The general evaluator widens unary minus to BIGINT. PostgreSQL folds a signed
    // integer literal first, including selectors and CASE/COALESCE alternatives.
    private static bool TrySignedIntegerLiteral(SqlExpression expression, out int index)
    {
        index = 0;
        return expression is SqlUnaryExpression { Operator: SqlUnaryOperator.Negate,
            Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } literal }
            && int.TryParse("-" + literal.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);
    }

    private SqlBoundExpression CoerceAlternative(SqlExpression syntax, SqlBoundExpression operand, DatabaseType type, Dictionary<SqlExpression, DatabaseType> types, bool jsonPosition = false)
    {
        while (syntax is SqlCollateExpression collate) { syntax = collate.Operand; }
        var source = AlternativeResultType(syntax, alternative: true, types);
        if (source == DatabaseType.Null && type == DatabaseType.Boolean && TryBooleanLiteral(syntax, out bool flag))
        { return new SqlBoundConstant(flag); }
        // Grouped projections retain their established output normalization. Their
        // keys and aggregate inputs bind in the source scope and use per-node casts.
        if (!jsonPosition && _typing?.SlotScope is not null && SqlFunctionResolver.IsNumeric(source) && SqlFunctionResolver.IsNumeric(type)) { return operand; }
        if (source == type && !SqlFunctionResolver.IsNumeric(type) || operand is SqlBoundConstant { Value: null }) { return operand; }
        var target = SqlType.ForStorage(new DatabaseTypeInfo(type));
        var cast = new SqlBoundCast(operand, target.Name, target, ResolveExpressionCasts(syntax, null, target, syntax.Location),
            _functions.Database, _defaultCollation, syntax.Location, source);
        // Unknown literal input conversion belongs to analysis, including skipped branches.
        if (syntax is not SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Integer }) { return cast; }
        try { return new SqlBoundConstant(Evaluate(cast, [])); }
        catch (SqlEvaluationException failure) when (syntax is SqlLiteralExpression { LiteralType: SqlLiteralType.String }
            && failure.Diagnostics[0].Code == SqlEvaluationException.CannotCastCode && type == DatabaseType.Boolean)
        {
            throw SqlEvaluationException.CastValueFailed(failure.Message, new FormatException(failure.Message, failure)).At(syntax.Location);
        }
    }
}
