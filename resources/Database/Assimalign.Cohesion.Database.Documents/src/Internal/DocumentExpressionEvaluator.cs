using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Execution.Internal;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    private readonly string? _alias;
    private readonly string? _writeVerb;
    private readonly DateTimeOffset? _transactionStart;
    private int _constructorMaxDepth = 256;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<OqlLikeExpression, int[]> _likePatterns = new();
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentExpressionEvaluator"/> class.
    /// </summary>
    /// <param name="alias">The collection alias that may prefix document paths, or <see langword="null"/> when the query declares none.</param>
    /// <param name="parameters">The OQL parameter values, or <see langword="null"/> when the statement supplies none.</param>
    /// <param name="cancellationToken">The statement cancellation token.</param>
    /// <param name="writeVerb">INSERT or UPSERT when constructors target the storage depth limit; otherwise null.</param>
    /// <param name="transactionStart">The UTC instant captured before the executing transaction began.</param>
    public DocumentExpressionEvaluator(string? alias, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken = default, string? writeVerb = null, DateTimeOffset? transactionStart = null)
    {
        _alias = alias;
        _writeVerb = writeVerb;
        _transactionStart = transactionStart;
        _cancellationToken = cancellationToken;
        _parameters = parameters;
    }

    internal object? Evaluate(OqlExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return expression switch
            {
                OqlLiteralExpression literal => Normalize(literal.Value),
                OqlMissingExpression => DocumentMissing.Instance,
                OqlParameterExpression parameter => Parameter(parameter.Name),
                OqlStarExpression => Normalize(document.Content),
                OqlMetaExpression meta => meta.Member == "id" ? document.Id : (decimal)document.Version,
                OqlArrayExpression array => DocumentJsonValue.Array(array.Elements.Select(element => Evaluate(element, document, group)), _writeVerb, _constructorMaxDepth),
                OqlObjectExpression obj => DocumentJsonValue.Object(obj.Members.Select(member =>
                    new KeyValuePair<string, object?>(member.Key, Evaluate(member.Value, document, group))), _writeVerb, _constructorMaxDepth),
                OqlPathExpression path => ReadPath(path, document.Content),
                OqlUnaryExpression unary => Unary(unary.Operator, Evaluate(unary.Operand, document, group)),
                OqlBinaryExpression binary => Binary(binary, document, group),
                OqlInExpression membership => In(membership, document, group),
                OqlBetweenExpression between => Between(between, document, group),
                OqlLikeExpression like => Like(like, document, group),
                OqlCaseExpression conditional => Case(conditional, document, group),
                OqlCallExpression call => Call(call, document, group),
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

    internal object? EvaluateProjection(OqlExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, bool bareValue)
    {
        // Named projection frames add one object level around the constructed value.
        _constructorMaxDepth = bareValue ? 256 : 255;
        try { return Evaluate(expression, document, group); }
        finally { _constructorMaxDepth = 256; }
    }

    internal bool Matches(OqlExpression? expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group = null)
        => expression is null || Truth(Evaluate(expression, document, group)) is true;

    // Check every reference and original JSON number before planning, including skipped
    // Boolean branches and expressions over empty inputs.
    internal void ValidateInputs(OqlExpression? expression)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression:
                foreach (var child in DocumentPlanner.PredicateChildren(expression)) { ValidateInputs(child); }
                break;
            case OqlLiteralExpression { Value: JsonElement } literal:
                _ = Evaluate(literal, default);
                break;
            case OqlParameterExpression parameter:
                if (_parameters is null || !_parameters.TryGetValue(ParameterBindings.BareName(parameter.Name), out var value))
                {
                    throw new DocumentPlanningException("COHDOC005", $"OQL parameter '{parameter.Name}' has no value.", parameter.Location);
                }
                if (value is JsonElement) { _ = Evaluate(parameter, default); }
                break;
            case OqlDeleteExpression delete:
                ValidateInputs(delete.Predicate);
                break;
            case OqlUpdateExpression update:
                foreach (var assignment in update.Assignments) { ValidateInputs(assignment.Value); }
                ValidateInputs(update.Predicate);
                break;
            case OqlInsertExpression insert:
                foreach (var pair in insert.Values) { ValidateInputs(pair.Key); ValidateInputs(pair.Value); }
                ValidateInputs(insert.Select);
                break;
            case OqlUpsertExpression upsert:
                foreach (var pair in upsert.Values) { ValidateInputs(pair.Key); ValidateInputs(pair.Value); }
                ValidateInputs(upsert.Select);
                break;
            case OqlMetaExpression meta:
                foreach (var argument in meta.Arguments) { ValidateInputs(argument); }
                break;
            case OqlArrayExpression array:
                foreach (var element in array.Elements) { ValidateInputs(element); }
                break;
            case OqlObjectExpression obj:
                foreach (var member in obj.Members) { ValidateInputs(member.Value); }
                break;
            case OqlSelectExpression select:
                _ = PagingCount(select.Limit, "LIMIT");
                _ = PagingCount(select.Offset, "OFFSET");
                foreach (var projection in select.Projections) { ValidateInputs(projection.Expression); }
                ValidateInputs(select.Predicate);
                foreach (var key in select.GroupBy) { ValidateInputs(key); }
                ValidateInputs(select.Having);
                foreach (var order in select.OrderBy) { ValidateInputs(order.Expression); }
                break;
            case OqlUnaryExpression unary:
                ValidateInputs(unary.Operand);
                break;
            case OqlBinaryExpression binary:
                ValidateInputs(binary.Left);
                ValidateInputs(binary.Right);
                break;
            case OqlCallExpression call:
                foreach (var argument in call.Arguments) { ValidateInputs(argument); }
                break;
        }
    }

    internal object? Parameter(string name) => Normalize(ParameterValue(name));

    private object? ParameterValue(string name)
    {
        if (_parameters is null || !_parameters.TryGetValue(ParameterBindings.BareName(name), out var value))
        {
            throw new DocumentPlanningException("COHDOC005", $"OQL parameter '{name}' has no value.");
        }
        return value;
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
        JsonElement json => NormalizeJson(json),
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
        bool or string => value,
        _ => throw new DocumentPlanningException("COHDOC008", "OQL parameters must be JSON scalars or JsonElement values."),
    };

    private static object? NormalizeJson(JsonElement json)
    {
        ValidateNumbers(json);
        return json.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => DocumentMissing.Instance,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number => json.GetDecimal(),
            _ => json,
        };
    }

    private static void ValidateNumbers(JsonElement json)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(json);
        while (pending.TryPop(out var current))
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Number:
                    try
                    {
                        DocumentStorage.ValidateContent(Encoding.UTF8.GetBytes(current.GetRawText()));
                    }
                    catch (JsonException exception)
                    {
                        throw new DocumentPlanningException("COHDOC007", ErrorMessages.NumberOutsideDecimalDomain, innerException: exception);
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject()) { pending.Push(property.Value); }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray()) { pending.Push(item); }
                    break;
            }
        }
    }

    private object? Binary(OqlBinaryExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var left = Evaluate(expression.Left, document, group);
        if (expression.Operator is "AND" or "OR") { left = Truth(left); }
        if (expression.Operator == "AND" && left is false) { return false; }
        if (expression.Operator == "OR" && left is true) { return true; }
        var right = Evaluate(expression.Right, document, group);
        if (expression.Operator is "AND" or "OR") { right = Truth(right); }
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
            "=" => Equal(left, right),
            "!=" or "<>" => Unary("NOT", Equal(left, right)),
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
        "NOT" => Truth(value) is bool boolean ? !boolean : null,
        "+" => value is null ? null : Number(value),
        "-" => value is null ? null : -Number(value),
        _ => throw new DocumentPlanningException("COHDOC001", $"OQL operator '{operation}' is not executable."),
    };

    private object? Aggregate(OqlCallExpression call, IReadOnlyList<DocumentEvaluationContext> group)
    {
        if (call.Arguments.Count != 1) { throw new DocumentPlanningException("COHDOC001", $"OQL aggregate '{call.Name}' requires one argument."); }
        if (call.Name == "COUNT" && call.Arguments[0] is OqlStarExpression) { return (long)group.Count; }
        var values = group.Select(document => Evaluate(call.Arguments[0], document)).Where(value => value is not null and not DocumentMissing).ToArray();
        if (call.Name == "COUNT") { return (long)values.Length; }
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

    private static decimal Number(object value) => value switch { decimal number => number, long number => number,
        _ => throw new DocumentPlanningException("COHDOC008", "OQL arithmetic requires numeric operands.") };
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
        decimal or long => 2,
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
