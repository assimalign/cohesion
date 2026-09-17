using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentExpressionEvaluator(string? alias, IReadOnlyDictionary<string, object?>? parameters)
{
    internal object? Evaluate(OqlExpression expression, JsonElement document, IReadOnlyList<JsonElement>? group = null)
        => expression switch
        {
            OqlLiteralExpression literal => Normalize(literal.Value),
            OqlParameterExpression parameter => Parameter(parameter.Name),
            OqlStarExpression => Normalize(document),
            OqlPathExpression path => ReadPath(path, document),
            OqlUnaryExpression unary => Unary(unary.Operator, Evaluate(unary.Operand, document, group)),
            OqlBinaryExpression binary => Binary(binary, document, group),
            OqlCallExpression call => Aggregate(call, group ?? throw new DatabaseException("An aggregate requires a grouping context.")),
            _ => throw new DatabaseException("The OQL expression is not executable."),
        };

    internal bool Matches(OqlExpression? expression, JsonElement document, IReadOnlyList<JsonElement>? group = null)
        => expression is null || Evaluate(expression, document, group) is true;

    internal object? Parameter(string name)
    {
        if (parameters is null || !parameters.TryGetValue(name, out var value))
        {
            throw new DatabaseException($"OQL parameter '{name}' has no value.");
        }
        return Normalize(value);
    }

    private object? ReadPath(OqlPathExpression path, JsonElement document)
    {
        int start = path.Segments.Count > 0 && alias is not null &&
            string.Equals(path.Segments[0].Name, alias, StringComparison.Ordinal) ? 1 : 0;
        var current = document;
        for (int i = start; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            if (segment.Name is string name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) { return null; }
            }
            else if (segment.Index is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength()) { return null; }
                current = current[index];
            }
        }
        return Normalize(current);
    }

    internal static object? Normalize(object? value) => value switch
    {
        null => null,
        JsonElement json => json.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number => json.TryGetDecimal(out var number) ? number :
                throw new DatabaseException("OQL numbers must fit the decimal scalar domain."),
            _ => json,
        },
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
        bool or string => value,
        _ => throw new DatabaseException("OQL parameters must be JSON scalars or JsonElement values."),
    };

    private object? Binary(OqlBinaryExpression expression, JsonElement document, IReadOnlyList<JsonElement>? group)
    {
        var left = Evaluate(expression.Left, document, group);
        if (expression.Operator == "AND" && left is false) { return false; }
        if (expression.Operator == "OR" && left is true) { return true; }
        var right = Evaluate(expression.Right, document, group);
        if (expression.Operator == "AND") { return left is true && right is true ? true : right is false ? false : null; }
        if (expression.Operator == "OR") { return left is true || right is true ? true : left is false && right is false ? false : null; }
        if (left is null || right is null) { return null; }
        return expression.Operator switch
        {
            "=" => Compare(left, right) == 0,
            "!=" or "<>" => Compare(left, right) != 0,
            "<" => SameScalarKind(left, right) && Compare(left, right) < 0,
            "<=" => SameScalarKind(left, right) && Compare(left, right) <= 0,
            ">" => SameScalarKind(left, right) && Compare(left, right) > 0,
            ">=" => SameScalarKind(left, right) && Compare(left, right) >= 0,
            "+" => Number(left) + Number(right),
            "-" => Number(left) - Number(right),
            "*" => Number(left) * Number(right),
            "/" => Number(right) == 0 ? throw new DatabaseException("OQL division by zero.") : Number(left) / Number(right),
            "%" => Number(right) == 0 ? throw new DatabaseException("OQL remainder by zero.") : Number(left) % Number(right),
            _ => throw new DatabaseException($"OQL operator '{expression.Operator}' is not executable."),
        };
    }

    private static object? Unary(string operation, object? value) => operation switch
    {
        "IS NULL" => value is null,
        "IS NOT NULL" => value is not null,
        "NOT" => value is bool boolean ? !boolean : null,
        "+" => value is null ? null : Number(value),
        "-" => value is null ? null : -Number(value),
        _ => throw new DatabaseException($"OQL operator '{operation}' is not executable."),
    };

    private object? Aggregate(OqlCallExpression call, IReadOnlyList<JsonElement> group)
    {
        if (call.Arguments.Count != 1) { throw new DatabaseException($"OQL aggregate '{call.Name}' requires one argument."); }
        if (call.Name == "COUNT" && call.Arguments[0] is OqlStarExpression) { return (decimal)group.Count; }
        var values = group.Select(document => Evaluate(call.Arguments[0], document)).Where(value => value is not null).ToArray();
        if (call.Name == "COUNT") { return (decimal)values.Length; }
        if (values.Length == 0) { return null; }
        return call.Name switch
        {
            "SUM" => values.Sum(value => Number(value!)),
            "AVG" => values.Average(value => Number(value!)),
            "MIN" => values.MinBy(value => value, ValueComparer.Instance),
            "MAX" => values.MaxBy(value => value, ValueComparer.Instance),
            _ => throw new DatabaseException($"OQL aggregate '{call.Name}' is not executable."),
        };
    }

    private static decimal Number(object value) => value is decimal number ? number : throw new DatabaseException("OQL arithmetic requires numeric operands.");
    private static bool SameScalarKind(object left, object right) => Kind(left) == Kind(right) && Kind(left) is >= 1 and <= 3;

    // Total order for grouping and sorting. Predicates only compare ranges within
    // a scalar kind; ordering can still deterministically place mixed shapes.
    internal static int Compare(object? left, object? right)
    {
        left = Normalize(left);
        right = Normalize(right);
        int kindOrder = Kind(left).CompareTo(Kind(right));
        if (kindOrder != 0) { return kindOrder; }
        return (left, right) switch
        {
            (null, null) => 0,
            (bool a, bool b) => a.CompareTo(b),
            (decimal a, decimal b) => a.CompareTo(b),
            (string a, string b) => StringComparer.Ordinal.Compare(a, b),
            (JsonElement a, JsonElement b) => CompareComposite(a, b),
            _ => 0,
        };
    }

    private static int Kind(object? value) => value switch
    {
        null => 0,
        bool => 1,
        decimal => 2,
        string => 3,
        JsonElement { ValueKind: JsonValueKind.Array } => 4,
        JsonElement { ValueKind: JsonValueKind.Object } => 5,
        _ => throw new DatabaseException("Unsupported OQL value."),
    };

    private static int CompareComposite(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < Math.Min(left.GetArrayLength(), right.GetArrayLength()); i++)
            {
                int comparison = Compare(left[i], right[i]);
                if (comparison != 0) { return comparison; }
            }
            return left.GetArrayLength().CompareTo(right.GetArrayLength());
        }
        var leftProperties = left.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        var rightProperties = right.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < Math.Min(leftProperties.Length, rightProperties.Length); i++)
        {
            int nameComparison = StringComparer.Ordinal.Compare(leftProperties[i].Name, rightProperties[i].Name);
            if (nameComparison != 0) { return nameComparison; }
            int valueComparison = Compare(leftProperties[i].Value, rightProperties[i].Value);
            if (valueComparison != 0) { return valueComparison; }
        }
        return leftProperties.Length.CompareTo(rightProperties.Length);
    }

    internal sealed class ValueComparer : IComparer<object?>
    {
        internal static ValueComparer Instance { get; } = new();
        public int Compare(object? left, object? right) => DocumentExpressionEvaluator.Compare(left, right);
    }
}
