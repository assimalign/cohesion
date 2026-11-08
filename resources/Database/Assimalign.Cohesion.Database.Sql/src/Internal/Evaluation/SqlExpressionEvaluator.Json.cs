using System;
using System.Collections.Generic;
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
        var operand = BindAlternativeOperand(json.Operand, out SqlCollationCandidate _, statisticsInput, types, jsonPosition: true);
        collation = default;
        return new SqlBoundIsJson(operand, source, json.Kind, json.IsNegated);
    }

    private DatabaseType JsonOperandType(SqlExpression expression, bool alternative = false, Dictionary<SqlExpression, DatabaseType>? types = null)
        => TrySignedIntegerLiteral(expression, out _) ? DatabaseType.Int32 : AlternativeResultType(expression, alternative, types, jsonPosition: true);

    private DatabaseType ValidateJsonOperand(SqlExpression expression, bool selector, bool predicate, Dictionary<SqlExpression, DatabaseType> types)
    {
        var type = JsonOperandType(expression, types: types);
        bool valid = predicate ? type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json or DatabaseType.JsonBinary or DatabaseType.Null
            : selector ? type is DatabaseType.String or DatabaseType.Character or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Null
            : type is DatabaseType.Json or DatabaseType.JsonBinary;
        if (!valid) { throw SqlJsonAccess.Signature(expression.Location); }
        return type;
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
