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
        if (IsBoundValue(expression))
        {
            var boundType = StaticTypeOf(expression);
            ValidateAlternativeSource(expression, boundType, jsonPosition, _typing?.SlotScope ?? this);
            return boundType;
        }
        if (expression is SqlCollateExpression collate) { return AlternativeResultType(collate.Operand, alternative, types, jsonPosition); }
        if (alternative && expression is SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Null }) { return DatabaseType.Null; }
        var type = types is not null && types.TryGetValue(expression, out var known)
            ? known : AlternativeResultTypeCore(expression, types, jsonPosition);
        // A completed all-NULL grouped alternative keeps its Null result contract
        // when another grouped alternative reads it, just as a native extrema slot does.
        if (alternative && !jsonPosition && _typing?.GroupedOutput == true && type == DatabaseType.String
            && (expression is SqlCaseExpression || expression is SqlFunctionCallExpression call && SqlStandardLibrary.IsCoalesce(call.FunctionName))
            && StaticTypeOf(expression) == DatabaseType.Null)
        { type = DatabaseType.Null; }
        if (types is not null) { types[expression] = type; }
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
            case SqlParameterExpression parameter when TryGetParameterValue(parameter, out var value)
                && value is not null && ValueTypeOf(value) == DatabaseType.Null:
                if (jsonPosition) { throw SqlJsonAccess.Signature(expression.Location); }
                throw UnresolvedAlternative(expression);
        }
        var result = TrySignedIntegerLiteral(expression, out _) ? DatabaseType.Int32 : StaticTypeOf(expression);
        ValidateAlternativeSource(expression, result, jsonPosition, this);
        return result;

        static DatabaseType Common(DatabaseType first, DatabaseType next, SqlExpression syntax)
        {
            if (first == DatabaseType.Null) { return next; }
            if (next == DatabaseType.Null || first == next) { return first; }
            if (first is DatabaseType.String or DatabaseType.Character && next is DatabaseType.String or DatabaseType.Character
                || SqlTemporalArithmetic.IsCalendar(first) && SqlTemporalArithmetic.IsCalendar(next)) { return AlternativeType(first, next); }
            if (SqlFunctionResolver.IsNumeric(first) && SqlFunctionResolver.IsNumeric(next)) { return ScalarNumericAlternative(first, next); }
            // JSON and JSONB have no implicit conversion, even though both are user types.
            throw new SqlPlanningException("CS02080", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.AlternativesMismatch, SqlType.NameOf(first), SqlType.NameOf(next)), syntax.Location);
        }
    }

    // A completed key/aggregate slot must not hide either named unresolved source.
    // Other Null-typed shapes and native extrema retain their established contracts.
    private void ValidateAlternativeSource(SqlExpression expression, DatabaseType type, bool jsonPosition, SqlExpressionEvaluator callScope)
    {
        if (type != DatabaseType.Null) { return; }
        while (expression is SqlCollateExpression collate) { expression = collate.Operand; }
        if (expression is SqlParameterExpression parameter && TryGetParameterValue(parameter, out var value)
            && value is not null && ValueTypeOf(value) == DatabaseType.Null)
        {
            if (jsonPosition) { throw SqlJsonAccess.Signature(expression.Location); }
            throw UnresolvedAlternative(expression);
        }
        if (!jsonPosition && expression is SqlFunctionCallExpression function
            && !SqlStandardLibrary.IsEvaluatorForm(function.FunctionName)
            && callScope.TypeCall(function).Function is { ReturnType.IsPseudo: true } resolved && resolved is not SqlExtremumFunction)
        { throw UnresolvedAlternative(expression); }
    }

    private static SqlPlanningException UnresolvedAlternative(SqlExpression expression)
        => new("CS02080", ErrorMessages.AlternativeTypeUnresolved, expression.Location);

    private static DatabaseType ScalarNumericAlternative(DatabaseType first, DatabaseType next)
    {
        if (first == DatabaseType.Float64 || next == DatabaseType.Float64) { return DatabaseType.Float64; }
        if (first == DatabaseType.Float32 || next == DatabaseType.Float32) { return DatabaseType.Float32; }
        if (first == DatabaseType.Decimal || next == DatabaseType.Decimal) { return DatabaseType.Decimal; }
        if (first == DatabaseType.Int64 || next == DatabaseType.Int64) { return DatabaseType.Int64; }
        if (first == DatabaseType.Int32 || next == DatabaseType.Int32) { return DatabaseType.Int32; }
        return first == DatabaseType.Int16 || next == DatabaseType.Int16 ? DatabaseType.Int16 : DatabaseType.Int8;
    }

    // Only grouped output needs the previous overload-selection type. Remember each
    // node once per scope; recomputing it inside every enclosing alternative was cubic.
    private DatabaseType StaticAlternativeType(SqlExpression expression)
    {
        if (_typing?.GroupedOutput != true) { return AlternativeResultType(expression); }
        if (_typing.Calls is { } types && _typing.IsCurrent(_subquerySlots?.Count ?? 0)
            && types.TryGetValue(expression, out var known)) { return known.Type; }
        var type = GroupedAlternativeBindingType(expression, AlternativeResultType(expression));
        _typing.Remember(expression, (null, type), _subquerySlots?.Count ?? 0);
        return type;
    }

    // Group output keeps its established normalization and overload typing. Only a
    // compatible legacy output family is retained; the shared resolver has already
    // checked every declared alternative. Newly admitted unknown inputs use its type.
    private DatabaseType GroupedAlternativeBindingType(SqlExpression expression, DatabaseType common, bool jsonPosition = false)
    {
        if (jsonPosition || _typing?.GroupedOutput != true) { return common; }
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
        // Grouped output keeps the value families native polymorphic calls observed
        // at base, including BIGINT signed literals. Keys and aggregate inputs use
        // the scalar scope, and JSON positions retain their own conversions.
        if (!jsonPosition && _typing?.GroupedOutput == true)
        { return Bind(expression, out collation, statisticsInput: statisticsInput); }
        if (TrySignedIntegerLiteral(expression, out int index)) { collation = default; return new SqlBoundConstant(index); }
        if (expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } integer
            && int.TryParse(integer.Value, NumberStyles.None, CultureInfo.InvariantCulture, out index))
        { collation = default; return new SqlBoundConstant(index); }
        if (expression is SqlUnaryExpression { Operator: SqlUnaryOperator.Plus,
            Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } positive }
            && int.TryParse(positive.Value, NumberStyles.None, CultureInfo.InvariantCulture, out index))
        { collation = default; return new SqlBoundConstant(index); }
        if (expression is SqlUnaryExpression { Operator: SqlUnaryOperator.Plus or SqlUnaryOperator.Negate,
            Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float } })
        { return new SqlBoundConstant(Evaluate(Bind(expression, out collation), [])); }
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
        var source = AlternativeResultType(syntax, alternative: true, types, jsonPosition: jsonPosition);
        while (syntax is SqlCollateExpression collate) { syntax = collate.Operand; }
        if (source == DatabaseType.Null && type == DatabaseType.Boolean && TryBooleanLiteral(syntax, out bool flag))
        { return new SqlBoundConstant(flag); }
        // Grouped projections retain their established output normalization. Their
        // keys and aggregate inputs bind in the source scope and use per-node casts.
        if (!jsonPosition && _typing?.GroupedOutput == true && SqlFunctionResolver.IsNumeric(source) && SqlFunctionResolver.IsNumeric(type)) { return operand; }
        if (_bindingContext == SqlBindingContext.IndexPredicate && source == type) { return operand; }
        if (source == type && !SqlFunctionResolver.IsNumeric(type) || operand is SqlBoundConstant { Value: null }) { return operand; }
        var target = SqlType.ForStorage(new DatabaseTypeInfo(type));
        // Declared alternatives use built-in implicit conversions only. Unknown
        // literals additionally use the built-in input conversion for their target.
        var conversions = SqlStandardCasts.AlternativeCasts(type,
            source == DatabaseType.Null && syntax is SqlLiteralExpression { LiteralType: SqlLiteralType.String });
        var cast = new SqlBoundCast(operand, target.Name, target, conversions,
            _functions.Database, _defaultCollation, syntax.Location, source);
        // Unknown literal input conversion belongs to analysis, including skipped branches.
        if (operand is not SqlBoundConstant) { return cast; }
        try { return new SqlBoundConstant(Evaluate(cast, [])); }
        catch (SqlEvaluationException failure) when (syntax is SqlLiteralExpression { LiteralType: SqlLiteralType.String }
            && failure.Diagnostics[0].Code == SqlEvaluationException.CannotCastCode && type == DatabaseType.Boolean)
        {
            throw SqlEvaluationException.CastValueFailed(failure.Message, new FormatException(failure.Message, failure)).At(syntax.Location);
        }
    }
}
