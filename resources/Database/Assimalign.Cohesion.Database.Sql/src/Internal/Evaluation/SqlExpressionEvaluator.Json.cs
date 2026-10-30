using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindIsJson(SqlIsJsonExpression json, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var types = new Dictionary<SqlExpression, DatabaseType>();
        var source = ValidateJsonOperand(json.Operand, selector: false, predicate: true, types);
        var operand = BindJsonOperand(json.Operand, out SqlCollationCandidate _, statisticsInput, types);
        collation = default;
        return new SqlBoundIsJson(operand, source, json.Kind, json.IsNegated);
    }

    // The ordinary CASE/COALESCE binder has no select_common_type equivalent. Keep
    // this resolution scoped to JSON positions; both forms use this one path.
    // PostgreSQL parse_expr.c:1742-1763,2244-2255; parse_coerce.c:1357-1456.
    private DatabaseType JsonOperandType(SqlExpression expression, bool alternative = false, Dictionary<SqlExpression, DatabaseType>? types = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlCollateExpression collate) { return JsonOperandType(collate.Operand, alternative, types); }
        if (alternative && expression is SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Null }) { return DatabaseType.Null; }
        if (types is not null && types.TryGetValue(expression, out var known)) { return known; }
        var type = JsonOperandTypeCore(expression, types);
        types?.Add(expression, type);
        return type;
    }

    private DatabaseType JsonOperandTypeCore(SqlExpression expression, Dictionary<SqlExpression, DatabaseType>? types)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case SqlCaseExpression conditional:
                var type = conditional.ElseResult is null ? DatabaseType.Null : JsonOperandType(conditional.ElseResult, alternative: true, types);
                foreach (var clause in conditional.WhenClauses) { type = Common(type, JsonOperandType(clause.Result, alternative: true, types), clause.Result); }
                return type == DatabaseType.Null ? DatabaseType.String : type;
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName):
                var common = DatabaseType.Null;
                foreach (var argument in call.Arguments) { common = Common(common, JsonOperandType(argument, alternative: true, types), argument); }
                return common == DatabaseType.Null ? DatabaseType.String : common;
            case SqlFunctionCallExpression call when call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count > 0:
                return JsonOperandType(call.Arguments[0], types: types);
            case SqlParameterExpression parameter when TryGetParameterValue(parameter, out var value) && value is not null && ValueTypeOf(value) == DatabaseType.Null:
                throw SqlJsonAccess.Signature(expression.Location);
        }
        return TryJsonIndexLiteral(expression, out _) ? DatabaseType.Int32 : StaticTypeOf(expression);

        static DatabaseType Common(DatabaseType first, DatabaseType next, SqlExpression syntax)
        {
            if (first == DatabaseType.Null) { return next; }
            if (next == DatabaseType.Null || first == next) { return first; }
            if (first is DatabaseType.String or DatabaseType.Character && next is DatabaseType.String or DatabaseType.Character
                || SqlTemporalArithmetic.IsCalendar(first) && SqlTemporalArithmetic.IsCalendar(next)) { return AlternativeType(first, next); }
            if (SqlFunctionResolver.IsNumeric(first) && SqlFunctionResolver.IsNumeric(next)) { return StatisticsAlternative(first, next); }
            // JSON and JSONB have no implicit conversion, even though both are user types.
            throw new SqlPlanningException("CS02080", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.JsonAlternativesMismatch, SqlType.NameOf(first), SqlType.NameOf(next)), syntax.Location);
        }
    }

    private DatabaseType ValidateJsonOperand(SqlExpression expression, bool selector, bool predicate, Dictionary<SqlExpression, DatabaseType> types)
    {
        var type = JsonOperandType(expression, types: types);
        bool valid = predicate ? type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json or DatabaseType.JsonBinary or DatabaseType.Null
            : selector ? type is DatabaseType.String or DatabaseType.Character or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Null
            : type is DatabaseType.Json or DatabaseType.JsonBinary;
        if (!valid) { throw SqlJsonAccess.Signature(expression.Location); }
        return type;
    }

    private SqlBoundExpression BindJsonOperand(SqlExpression expression, out SqlCollationCandidate collation, bool statisticsInput, Dictionary<SqlExpression, DatabaseType> types)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (_valueOrdinals is not null && _valueOrdinals.ContainsKey(expression))
        { return Bind(expression, out collation, statisticsInput: statisticsInput); }
        switch (expression)
        {
            case SqlCaseExpression conditional: return BindCase(conditional, out collation, false, statisticsInput, types);
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName)
                || call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase):
                return BindCall(call, out collation, false, statisticsInput, types);
            case SqlCollateExpression collate:
                var operand = BindJsonOperand(collate.Operand, out var candidate, statisticsInput, types);
                collation = CollationOf(collate, [candidate]);
                return operand;
        }
        if (TryJsonIndexLiteral(expression, out int index)) { collation = default; return new SqlBoundConstant(index); }
        return Bind(expression, out collation, statisticsInput: statisticsInput);
    }

    private SqlBoundExpression CoerceJsonAlternative(SqlExpression syntax, SqlBoundExpression operand, DatabaseType type, Dictionary<SqlExpression, DatabaseType> types)
    {
        var source = JsonOperandType(syntax, alternative: true, types);
        if (source == type || operand is SqlBoundConstant { Value: null }) { return operand; }
        var target = SqlType.ForStorage(new DatabaseTypeInfo(type));
        var cast = new SqlBoundCast(operand, target.Name, target, ResolveExpressionCasts(syntax, null, target, syntax.Location),
            _functions.Database, _defaultCollation, syntax.Location, source);
        // Unknown literal input conversion belongs to analysis, including skipped branches.
        return syntax is SqlLiteralExpression { LiteralType: SqlLiteralType.String }
            ? new SqlBoundConstant(Evaluate(cast, [])) : cast;
    }

    // The general evaluator widens unary minus to BIGINT. PostgreSQL folds a signed
    // integer literal first; keep that INTEGER selector signature local to arrows.
    private static bool TryJsonIndexLiteral(SqlExpression expression, out int index)
    {
        index = 0;
        return expression is SqlUnaryExpression { Operator: SqlUnaryOperator.Negate,
            Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } literal }
            && int.TryParse("-" + literal.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);
    }

    private object? EvaluateJsonAccess(SqlBoundJsonAccess expression, object?[] row)
    {
        var operand = EvaluateCore(expression.Operand, row);
        JsonDocument? document = null;
        JsonElement? selected = null;
        bool missing = operand is null;
        try
        {
            foreach (var step in expression.Steps)
            {
                // Even after a missing step, later selectors still evaluate, as nested
                // strict arrows did. Do not evaluate them before an earlier access fails.
                var selector = EvaluateCore(step.Selector, row);
                if (missing || selector is null) { missing = true; continue; }
                try
                {
                    document ??= SqlJsonAccess.ReadExtraction(operand!);
                    selected = SqlJsonAccess.Select(selected ?? document.RootElement, selector, step.IsIndex);
                    missing = selected is null;
                }
                catch (SqlExecutionException exception) { throw exception.At(step.Location); }
            }
            return missing ? null : SqlJsonAccess.Serialize(selected!.Value, expression.Source, expression.AsText);
        }
        catch (SqlExecutionException exception) { throw exception.At(expression.Steps[^1].Location); }
        finally { document?.Dispose(); }
    }

    private object? EvaluateIsJson(SqlBoundIsJson expression, object?[] row)
    {
        var operand = EvaluateCore(expression.Operand, row);
        return operand is null ? null : SqlJsonAccess.IsJson(operand, expression.JsonKind) != expression.IsNegated;
    }
}
