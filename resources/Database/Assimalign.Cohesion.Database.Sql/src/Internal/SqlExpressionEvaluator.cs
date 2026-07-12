using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Evaluates SQL scalar expressions against a row. Null propagates SQL-style:
/// any null operand makes a comparison or arithmetic result null, and a null
/// predicate result filters the row out.
/// </summary>
internal sealed class SqlExpressionEvaluator
{
    private readonly IReadOnlyList<SqlCatalogColumn> _columns;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    internal SqlExpressionEvaluator(IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyDictionary<string, object?>? parameters)
    {
        _columns = columns;
        _parameters = parameters;
    }

    /// <summary>
    /// Evaluates a predicate: true only when the expression evaluates to true
    /// (false and null both reject the row).
    /// </summary>
    internal bool Matches(SqlExpression? predicate, object?[] row)
    {
        if (predicate is null)
        {
            return true;
        }

        return Evaluate(predicate, row) is true;
    }

    internal object? Evaluate(SqlExpression expression, object?[] row)
    {
        return expression switch
        {
            SqlLiteralExpression literal => EvaluateLiteral(literal),
            SqlColumnReferenceExpression column => row[ResolveColumn(column)],
            SqlParameterExpression parameter => ResolveParameter(parameter),
            SqlBinaryExpression binary => EvaluateBinary(binary, row),
            SqlUnaryExpression unary => EvaluateUnary(unary, row),
            SqlIsNullExpression isNull => EvaluateIsNull(isNull, row),
            SqlBetweenExpression between => EvaluateBetween(between, row),
            SqlInExpression inExpression => EvaluateIn(inExpression, row),
            SqlLikeExpression like => EvaluateLike(like, row),
            SqlCaseExpression caseExpression => EvaluateCase(caseExpression, row),
            SqlFunctionCallExpression function => EvaluateFunction(function, row),
            SqlCastExpression cast => Evaluate(cast.Operand, row), // storage types coerce at write; CAST is a planner hint for now
            _ => throw new DatabaseException($"Expression '{expression.GetType().Name}' is not supported by the executor yet."),
        };
    }

    internal int ResolveColumn(SqlColumnReferenceExpression column)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i].Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new DatabaseException($"Unknown column '{column.ColumnName}'.");
    }

    private static object? EvaluateLiteral(SqlLiteralExpression literal)
    {
        return literal.LiteralType switch
        {
            SqlLiteralType.Null => null,
            SqlLiteralType.String => literal.Value,
            SqlLiteralType.Boolean => literal.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
            SqlLiteralType.Integer => long.Parse(literal.Value, CultureInfo.InvariantCulture),
            SqlLiteralType.Float => decimal.Parse(literal.Value, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => throw new DatabaseException($"Literal type {literal.LiteralType} is not supported."),
        };
    }

    private object? ResolveParameter(SqlParameterExpression parameter)
    {
        // The AST keeps the sigil ('@name' / '$1'); callers bind by bare name.
        string name = parameter.ParameterName.TrimStart('@', '$');

        if (_parameters is not null &&
            (_parameters.TryGetValue(name, out var value) || _parameters.TryGetValue(parameter.ParameterName, out value)))
        {
            return value;
        }

        throw new DatabaseException($"No value was supplied for parameter '{name}'.");
    }

    private object? EvaluateBinary(SqlBinaryExpression binary, object?[] row)
    {
        // Logical operators get SQL three-valued treatment over nullable booleans.
        if (binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or)
        {
            bool? left = Evaluate(binary.Left, row) as bool?;
            bool? right = Evaluate(binary.Right, row) as bool?;

            return binary.Operator == SqlBinaryOperator.And
                ? (left, right) switch
                {
                    (false, _) or (_, false) => false,
                    (true, true) => true,
                    _ => null,
                }
                : (left, right) switch
                {
                    (true, _) or (_, true) => true,
                    (false, false) => false,
                    _ => (object?)null,
                };
        }

        object? leftValue = Evaluate(binary.Left, row);
        object? rightValue = Evaluate(binary.Right, row);

        if (leftValue is null || rightValue is null)
        {
            return null; // SQL null propagation
        }

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => Compare(leftValue, rightValue) == 0,
            SqlBinaryOperator.NotEqual => Compare(leftValue, rightValue) != 0,
            SqlBinaryOperator.LessThan => Compare(leftValue, rightValue) < 0,
            SqlBinaryOperator.GreaterThan => Compare(leftValue, rightValue) > 0,
            SqlBinaryOperator.LessOrEqual => Compare(leftValue, rightValue) <= 0,
            SqlBinaryOperator.GreaterOrEqual => Compare(leftValue, rightValue) >= 0,
            SqlBinaryOperator.Add => Arithmetic(leftValue, rightValue, static (a, b) => a + b, static (a, b) => a + b),
            SqlBinaryOperator.Subtract => Arithmetic(leftValue, rightValue, static (a, b) => a - b, static (a, b) => a - b),
            SqlBinaryOperator.Multiply => Arithmetic(leftValue, rightValue, static (a, b) => a * b, static (a, b) => a * b),
            SqlBinaryOperator.Divide => Arithmetic(leftValue, rightValue, static (a, b) => a / b, static (a, b) => a / b),
            SqlBinaryOperator.Modulo => Arithmetic(leftValue, rightValue, static (a, b) => a % b, static (a, b) => a % b),
            SqlBinaryOperator.Concat => Convert.ToString(leftValue, CultureInfo.InvariantCulture) + Convert.ToString(rightValue, CultureInfo.InvariantCulture),
            _ => throw new DatabaseException($"Operator {binary.Operator} is not supported by the executor yet."),
        };
    }

    private object? EvaluateUnary(SqlUnaryExpression unary, object?[] row)
    {
        object? operand = Evaluate(unary.Operand, row);

        if (operand is null)
        {
            return null;
        }

        return unary.Operator switch
        {
            SqlUnaryOperator.Negate => operand switch
            {
                long value => -value,
                int value => -(long)value,
                double value => -value,
                float value => -(double)value,
                decimal value => -value,
                _ => throw new DatabaseException($"Cannot negate a value of type {operand.GetType().Name}."),
            },
            SqlUnaryOperator.Not => operand is bool flag ? !flag : throw new DatabaseException("NOT requires a boolean operand."),
            _ => throw new DatabaseException($"Unary operator {unary.Operator} is not supported."),
        };
    }

    private object? EvaluateIsNull(SqlIsNullExpression expression, object?[] row)
    {
        bool isNull = Evaluate(expression.Operand, row) is null;
        return expression.IsNegated ? !isNull : isNull;
    }

    private object? EvaluateBetween(SqlBetweenExpression expression, object?[] row)
    {
        object? value = Evaluate(expression.Operand, row);
        object? lower = Evaluate(expression.Low, row);
        object? upper = Evaluate(expression.High, row);

        if (value is null || lower is null || upper is null)
        {
            return null;
        }

        bool between = Compare(value, lower) >= 0 && Compare(value, upper) <= 0;
        return expression.IsNegated ? !between : between;
    }

    private object? EvaluateIn(SqlInExpression expression, object?[] row)
    {
        if (expression.Values is null)
        {
            throw new DatabaseException("IN subqueries are not supported by the executor yet.");
        }

        object? value = Evaluate(expression.Operand, row);

        if (value is null)
        {
            return null;
        }

        foreach (var candidate in expression.Values)
        {
            object? candidateValue = Evaluate(candidate, row);

            if (candidateValue is not null && Compare(value, candidateValue) == 0)
            {
                return !expression.IsNegated;
            }
        }

        return expression.IsNegated;
    }

    private object? EvaluateLike(SqlLikeExpression expression, object?[] row)
    {
        object? value = Evaluate(expression.Operand, row);
        object? pattern = Evaluate(expression.Pattern, row);

        if (value is null || pattern is null)
        {
            return null;
        }

        bool matches = LikeMatches(
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(pattern, CultureInfo.InvariantCulture) ?? string.Empty);

        return expression.IsNegated ? !matches : matches;
    }

    private object? EvaluateCase(SqlCaseExpression expression, object?[] row)
    {
        object? input = expression.Input is null ? null : Evaluate(expression.Input, row);

        foreach (var when in expression.WhenClauses)
        {
            if (expression.Input is null)
            {
                if (Evaluate(when.Condition, row) is true)
                {
                    return Evaluate(when.Result, row);
                }
            }
            else
            {
                object? candidate = Evaluate(when.Condition, row);

                if (input is not null && candidate is not null && Compare(input, candidate) == 0)
                {
                    return Evaluate(when.Result, row);
                }
            }
        }

        return expression.ElseResult is null ? null : Evaluate(expression.ElseResult, row);
    }

    private object? EvaluateFunction(SqlFunctionCallExpression function, object?[] row)
    {
        string name = function.FunctionName.ToUpperInvariant();

        if (name == "COALESCE")
        {
            foreach (var argument in function.Arguments)
            {
                object? value = Evaluate(argument, row);

                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        object? single = function.Arguments.Count == 1 ? Evaluate(function.Arguments[0], row) : null;

        return name switch
        {
            "UPPER" => (single as string)?.ToUpperInvariant() ?? single,
            "LOWER" => (single as string)?.ToLowerInvariant() ?? single,
            "LENGTH" => single is null ? null : (long)(Convert.ToString(single, CultureInfo.InvariantCulture)?.Length ?? 0),
            "ABS" => single switch
            {
                null => null,
                long value => Math.Abs(value),
                int value => (long)Math.Abs(value),
                double value => Math.Abs(value),
                decimal value => Math.Abs(value),
                _ => throw new DatabaseException("ABS requires a numeric argument."),
            },
            _ => throw new DatabaseException($"Function '{function.FunctionName}' is not supported by the executor yet."),
        };
    }

    /// <summary>
    /// Compares two non-null values with numeric promotion (integers and floats
    /// promote to decimal/double) and ordinal string comparison.
    /// </summary>
    internal static int Compare(object left, object right)
    {
        if (TryToNumber(left, out decimal leftNumber) && TryToNumber(right, out decimal rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is string leftText && right is string rightText)
        {
            return Types.Collation.Binary.Compare(leftText, rightText);
        }

        if (left is bool leftFlag && right is bool rightFlag)
        {
            return leftFlag.CompareTo(rightFlag);
        }

        if (left.GetType() == right.GetType() && left is IComparable comparable)
        {
            return comparable.CompareTo(right);
        }

        throw new DatabaseException($"Cannot compare values of types {left.GetType().Name} and {right.GetType().Name}.");
    }

    private static bool TryToNumber(object value, out decimal number)
    {
        switch (value)
        {
            case sbyte v: number = v; return true;
            case short v: number = v; return true;
            case int v: number = v; return true;
            case long v: number = v; return true;
            case float v: number = (decimal)v; return true;
            case double v: number = (decimal)v; return true;
            case decimal v: number = v; return true;
            default: number = 0; return false;
        }
    }

    private static object? Arithmetic(
        object left,
        object right,
        Func<decimal, decimal, decimal> decimalOperation,
        Func<long, long, long> integerOperation)
    {
        bool integers = left is sbyte or short or int or long && right is sbyte or short or int or long;

        if (!TryToNumber(left, out decimal a) || !TryToNumber(right, out decimal b))
        {
            throw new DatabaseException("Arithmetic requires numeric operands.");
        }

        if (integers)
        {
            return integerOperation(Convert.ToInt64(left, CultureInfo.InvariantCulture), Convert.ToInt64(right, CultureInfo.InvariantCulture));
        }

        return decimalOperation(a, b);
    }

    /// <summary>
    /// SQL LIKE with <c>%</c> (any run) and <c>_</c> (any one character), ordinal.
    /// </summary>
    internal static bool LikeMatches(string input, string pattern)
    {
        return Matches(input.AsSpan(), pattern.AsSpan());

        static bool Matches(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
        {
            while (!pattern.IsEmpty)
            {
                char token = pattern[0];

                if (token == '%')
                {
                    // Collapse the wildcard, then try every suffix.
                    var rest = pattern[1..];

                    if (rest.IsEmpty)
                    {
                        return true;
                    }

                    for (int i = 0; i <= input.Length; i++)
                    {
                        if (Matches(input[i..], rest))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (input.IsEmpty)
                {
                    return false;
                }

                if (token != '_' && input[0] != token)
                {
                    return false;
                }

                input = input[1..];
                pattern = pattern[1..];
            }

            return input.IsEmpty;
        }
    }
}
