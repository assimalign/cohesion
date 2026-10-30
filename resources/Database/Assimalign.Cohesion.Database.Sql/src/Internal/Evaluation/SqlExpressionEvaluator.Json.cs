using System;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindIsJson(SqlIsJsonExpression json, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var operand = Bind(json.Operand, out SqlCollationCandidate _, statisticsInput: statisticsInput);
        ValidateJsonOperand(json.Operand, selector: false, predicate: true);
        collation = default;
        return new SqlBoundIsJson(operand, json.Kind, json.IsNegated);
    }

    // CASE/COALESCE's chosen static type cannot authorize a different result alternative.
    // Walk result alternatives only; the ordinary binder owns conditions and explicit CAST inputs.
    private DatabaseType ValidateJsonOperand(SqlExpression expression, bool selector, bool predicate, bool allowUntypedNull = false)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var type = selector && TryJsonIndexLiteral(expression, out _) ? DatabaseType.Int32 : StaticTypeOf(expression);
        // Unknown NULL alternatives can adopt a surrounding JSON result type;
        // an unresolved root operand cannot choose between JSON and JSONB.
        if (!selector && !predicate && type == DatabaseType.Null && allowUntypedNull && expression is not
            (SqlLiteralExpression { LiteralType: SqlLiteralType.Null } or SqlParameterExpression or SqlCollateExpression or SqlCaseExpression)
            && !(expression is SqlFunctionCallExpression nullWrapper && (SqlStandardLibrary.IsCoalesce(nullWrapper.FunctionName)
                || nullWrapper.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase))))
        { throw SqlJsonAccess.Signature(expression.Location); }
        bool valid = predicate ? type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json or DatabaseType.JsonBinary or DatabaseType.Null
            : selector ? type is DatabaseType.String or DatabaseType.Character or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Null
            : type is DatabaseType.Json or DatabaseType.JsonBinary || type == DatabaseType.Null && allowUntypedNull;
        if (!valid) { throw SqlJsonAccess.Signature(expression.Location); }
        switch (expression)
        {
            case SqlParameterExpression parameter when TryGetParameterValue(parameter, out var value) && value is not null && type == DatabaseType.Null:
                throw SqlJsonAccess.Signature(expression.Location);
            case SqlCollateExpression collate:
                ValidateJsonOperand(collate.Operand, selector, predicate, allowUntypedNull);
                break;
            case SqlCaseExpression conditional:
                foreach (var clause in conditional.WhenClauses) { ValidateAlternative(clause.Result); }
                if (conditional.ElseResult is not null) { ValidateAlternative(conditional.ElseResult); }
                break;
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName):
                foreach (var argument in call.Arguments) { ValidateAlternative(argument); }
                break;
            case SqlFunctionCallExpression call when call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count > 0:
                ValidateAlternative(call.Arguments[0]);
                break;
        }
        return type;

        void ValidateAlternative(SqlExpression alternative)
        {
            var other = ValidateJsonOperand(alternative, selector, predicate, allowUntypedNull: true);
            if (!selector && !predicate && type != DatabaseType.Null && other != DatabaseType.Null && type != other)
            { throw SqlJsonAccess.Signature(alternative.Location); }
        }
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
        var selector = EvaluateCore(expression.Selector, row);
        if (operand is null || selector is null) { return null; }
        try { return SqlJsonAccess.Extract(operand, selector, expression.Source, expression.AsText); }
        catch (SqlExecutionException exception) { throw exception.At(expression.Location); }
    }

    private object? EvaluateIsJson(SqlBoundIsJson expression, object?[] row)
    {
        var operand = EvaluateCore(expression.Operand, row);
        return operand is null ? null : SqlJsonAccess.IsJson(operand, expression.JsonKind) != expression.IsNegated;
    }
}
