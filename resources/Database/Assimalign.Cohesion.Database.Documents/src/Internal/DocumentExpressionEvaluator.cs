using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Assimalign.Cohesion.Database.Documents.Language;

using Assimalign.Cohesion.Database.Execution.Internal;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentExpressionEvaluator
{
    private readonly string? _alias;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentExpressionEvaluator"/> class.
    /// </summary>
    /// <param name="alias">The collection alias that may prefix document paths, or <see langword="null"/> when the query declares none.</param>
    /// <param name="parameters">The OQL parameter values, or <see langword="null"/> when the statement supplies none.</param>
    public DocumentExpressionEvaluator(string? alias, IReadOnlyDictionary<string, object?>? parameters)
    {
        _alias = alias;
        _parameters = parameters;
    }

    internal object? Evaluate(OqlExpression expression, JsonElement document, IReadOnlyList<JsonElement>? group = null)
    {
        try
        {
            return expression switch
            {
                OqlLiteralExpression literal => Normalize(literal.Value),
                OqlMissingExpression => DocumentMissing.Instance,
                OqlParameterExpression parameter => Parameter(parameter.Name),
                OqlStarExpression => Normalize(document),
                OqlPathExpression path => ReadPath(path, document),
                OqlUnaryExpression unary => Unary(unary.Operator, Evaluate(unary.Operand, document, group)),
                OqlBinaryExpression binary => Binary(binary, document, group),
                OqlCallExpression call => Aggregate(call, group ?? throw new DocumentPlanningException("COHDOC001", "An aggregate requires a grouping context.")),
                _ => throw new DocumentPlanningException("COHDOC001", "The OQL expression is not executable."),
            };
        }
        catch (DocumentPlanningException exception) when (exception.Diagnostics[0].Start is null && expression.Location is not null)
        {
            throw exception.At(expression.Location);
        }
        catch (OverflowException exception)
        {
            throw new DocumentPlanningException("COHDOC007", "OQL numbers must fit the decimal scalar domain.", expression.Location, exception);
        }
    }

    internal bool Matches(OqlExpression? expression, JsonElement document, IReadOnlyList<JsonElement>? group = null)
        => expression is null || Evaluate(expression, document, group) is true;

    // Check every use, including skipped Boolean branches and expressions over empty inputs.
    internal void ValidateParameters(OqlExpression? expression)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case OqlParameterExpression parameter:
                if (_parameters is null || !_parameters.ContainsKey(ParameterBindings.BareName(parameter.Name)))
                {
                    throw new DocumentPlanningException("COHDOC005", $"OQL parameter '{parameter.Name}' has no value.", parameter.Location);
                }
                break;
            case OqlSelectExpression select:
                foreach (var projection in select.Projections) { ValidateParameters(projection.Expression); }
                ValidateParameters(select.Predicate);
                foreach (var key in select.GroupBy) { ValidateParameters(key); }
                ValidateParameters(select.Having);
                foreach (var order in select.OrderBy) { ValidateParameters(order.Expression); }
                break;
            case OqlUnaryExpression unary:
                ValidateParameters(unary.Operand);
                break;
            case OqlBinaryExpression binary:
                ValidateParameters(binary.Left);
                ValidateParameters(binary.Right);
                break;
            case OqlCallExpression call:
                foreach (var argument in call.Arguments) { ValidateParameters(argument); }
                break;
        }
    }

    internal object? Parameter(string name)
    {
        if (_parameters is null || !_parameters.TryGetValue(ParameterBindings.BareName(name), out var value))
        {
            throw new DocumentPlanningException("COHDOC005", $"OQL parameter '{name}' has no value.");
        }
        return Normalize(value);
    }

    private object? ReadPath(OqlPathExpression path, JsonElement document)
    {
        int start = path.Segments.Count > 0 && _alias is not null &&
            string.Equals(path.Segments[0].Name, _alias, StringComparison.Ordinal) ? 1 : 0;
        var current = document;
        for (int i = start; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            if (segment.Name is string name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) { return DocumentMissing.Instance; }
            }
            else if (segment.Index is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength()) { return DocumentMissing.Instance; }
                current = current[index];
            }
        }
        return Normalize(current);
    }

    internal static object? Normalize(object? value)
    {
        try
        {
            return NormalizeCore(value);
        }
        catch (OverflowException exception)
        {
            throw new DocumentPlanningException("COHDOC007", "OQL numbers must fit the decimal scalar domain.", innerException: exception);
        }
    }

    private static object? NormalizeCore(object? value) => value switch
    {
        null => null,
        DocumentMissing => value,
        JsonElement json => json.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => DocumentMissing.Instance,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number => json.TryGetDecimal(out var number) ? number :
                throw new DocumentPlanningException("COHDOC007", "OQL numbers must fit the decimal scalar domain."),
            _ => json,
        },
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
        bool or string => value,
        _ => throw new DocumentPlanningException("COHDOC008", "OQL parameters must be JSON scalars or JsonElement values."),
    };

    private object? Binary(OqlBinaryExpression expression, JsonElement document, IReadOnlyList<JsonElement>? group)
    {
        var left = Evaluate(expression.Left, document, group);
        if (expression.Operator == "AND" && left is false) { return false; }
        if (expression.Operator == "OR" && left is true) { return true; }
        var right = Evaluate(expression.Right, document, group);
        if (expression.Operator == "AND")
        {
            if (right is false) { return false; }
            if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
            return left is true && right is true ? true : null;
        }
        if (expression.Operator == "OR")
        {
            if (right is true) { return true; }
            if (left is null || right is null) { return null; }
            if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
            return left is false && right is false ? false : null;
        }
        if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
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
            "/" => Number(right) == 0 ? throw new DocumentPlanningException("COHDOC006", "OQL division by zero.") : Number(left) / Number(right),
            "%" => Number(right) == 0 ? throw new DocumentPlanningException("COHDOC006", "OQL remainder by zero.") : Number(left) % Number(right),
            _ => throw new DocumentPlanningException("COHDOC001", $"OQL operator '{expression.Operator}' is not executable."),
        };
    }

    private static object? Unary(string operation, object? value) => operation switch
    {
        "IS MISSING" => value is DocumentMissing,
        "IS NOT MISSING" => value is not DocumentMissing,
        "IS VALUED" => value is not null and not DocumentMissing,
        "IS NOT VALUED" => value is null or DocumentMissing,
        _ when value is DocumentMissing => DocumentMissing.Instance,
        "IS NULL" => value is null,
        "IS NOT NULL" => value is not null,
        "NOT" => value is bool boolean ? !boolean : null,
        "+" => value is null ? null : Number(value),
        "-" => value is null ? null : -Number(value),
        _ => throw new DocumentPlanningException("COHDOC001", $"OQL operator '{operation}' is not executable."),
    };

    private object? Aggregate(OqlCallExpression call, IReadOnlyList<JsonElement> group)
    {
        if (call.Arguments.Count != 1) { throw new DocumentPlanningException("COHDOC001", $"OQL aggregate '{call.Name}' requires one argument."); }
        if (call.Name == "COUNT" && call.Arguments[0] is OqlStarExpression) { return (decimal)group.Count; }
        var values = group.Select(document => Evaluate(call.Arguments[0], document)).Where(value => value is not null and not DocumentMissing).ToArray();
        if (call.Name == "COUNT") { return (decimal)values.Length; }
        if (values.Length == 0) { return null; }
        return call.Name switch
        {
            "SUM" => values.Sum(value => Number(value!)),
            "AVG" => values.Average(value => Number(value!)),
            "MIN" => values.MinBy(value => value, ValueComparer.Instance),
            "MAX" => values.MaxBy(value => value, ValueComparer.Instance),
            _ => throw new DocumentPlanningException("COHDOC001", $"OQL aggregate '{call.Name}' is not executable."),
        };
    }

    private static decimal Number(object value) => value is decimal number ? number : throw new DocumentPlanningException("COHDOC008", "OQL arithmetic requires numeric operands.");
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
        DocumentMissing => -1,
        null => 0,
        bool => 1,
        decimal => 2,
        string => 3,
        JsonElement { ValueKind: JsonValueKind.Array } => 4,
        JsonElement { ValueKind: JsonValueKind.Object } => 5,
        _ => throw new DocumentPlanningException("COHDOC008", "Unsupported OQL value."),
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
