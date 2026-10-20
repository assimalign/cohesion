using System;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    internal long? PagingCount(OqlExpression? expression, string clause)
    {
        if (expression is null) { return null; }
        var literal = expression is OqlUnaryExpression { Operator: "+" or "-", Operand: OqlLiteralExpression } unary
            ? unary.Operand : expression;
        bool valid = literal is OqlLiteralExpression || expression is OqlParameterExpression;
        // A direct AST cannot smuggle a fractional/exponent literal or an expression into paging.
        if (literal is OqlLiteralExpression { Value: JsonElement json })
        {
            valid = json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out _);
        }
        try
        {
            if (valid && Evaluate(expression, default) is decimal number && number >= 0 &&
                number <= long.MaxValue && decimal.Truncate(number) == number)
            {
                // CLR floating-point normalization can round a fraction to an integer or
                // underflow it to zero. Counts must still reject that original fraction.
                var raw = expression is OqlParameterExpression parameter ? ParameterValue(parameter.Name) :
                    literal is OqlLiteralExpression value ? value.Value : null;
                if (expression is OqlUnaryExpression { Operator: "-" })
                {
                    raw = raw switch { double signedDouble => -signedDouble, float signedSingle => -signedSingle, _ => raw };
                }
                if (raw is double floating)
                {
                    if (double.IsFinite(floating) && floating >= 0 && floating < 9223372036854775808d &&
                        Math.Truncate(floating) == floating) { return (long)floating; }
                }
                else if (raw is float single)
                {
                    if (float.IsFinite(single) && single >= 0 && single < 9223372036854775808f &&
                        MathF.Truncate(single) == single) { return (long)single; }
                }
                else { return (long)number; }
            }
        }
        catch (DocumentPlanningException exception) when (exception.Diagnostics[0].Code is "COHDOC007" or "COHDOC008")
        {
            throw new DocumentPlanningException("OQL0007",
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PagingCountInvalid, clause), expression.Location, exception);
        }
        throw new DocumentPlanningException("OQL0007",
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.PagingCountInvalid, clause), expression.Location);
    }
}
