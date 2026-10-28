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

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentExpressionEvaluator")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentExpressionEvaluator.DebugView))]
internal sealed partial class DocumentExpressionEvaluator
{
    private readonly DocumentFunctionCatalog _functions;
    private readonly Assimalign.Cohesion.Database.DatabaseName _databaseName;
    private Dictionary<OqlCallExpression, DocumentFunction>? _resolvedFunctions;
    private readonly string? _alias;
    private readonly string? _starSourceName;
    private readonly string? _writeVerb;
    private readonly string _resultClause;
    private readonly DateTimeOffset? _transactionStart;
    private int _constructorMaxDepth = 256;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<OqlLikeExpression, int[]> _likePatterns = new();
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly Dictionary<string, object?> _normalizedParameters = new(StringComparer.Ordinal);
    private EvaluationState? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentExpressionEvaluator"/> class.
    /// </summary>
    /// <param name="alias">The collection alias that may prefix document paths, or <see langword="null"/> when the query declares none.</param>
    /// <param name="parameters">The OQL parameter values, or <see langword="null"/> when the statement supplies none.</param>
    /// <param name="cancellationToken">The statement cancellation token.</param>
    /// <param name="writeVerb">INSERT or UPSERT when constructors target the storage depth limit; otherwise null.</param>
    /// <param name="transactionStart">The UTC instant captured before the executing transaction began.</param>
    /// <param name="starSourceName">The FROM object member name for a synthetic UNNEST star, or null for a stored star.</param>
    /// <param name="resultClause">The result clause named by constructor-depth diagnostics.</param>
    /// <param name="operation">The executing statement that owns retained-value accounting, or null during planning.</param>
    public DocumentExpressionEvaluator(string? alias, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken = default, string? writeVerb = null, DateTimeOffset? transactionStart = null, string? starSourceName = null, DocumentFunctionCatalog? functions = null, DatabaseName databaseName = default, string resultClause = "SELECT", DocumentOperation? operation = null)
    {
        _functions = functions ?? DocumentFunctionCatalog.Standard;
        _databaseName = databaseName;
        _alias = alias;
        _starSourceName = starSourceName;
        _writeVerb = writeVerb;
        _resultClause = resultClause;
        _state = operation is null ? null : new EvaluationState(operation);
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
                OqlStarExpression => Star(document),
                OqlMetaExpression meta => meta.Member == "id" ? document.Id : (decimal)document.Version,
                OqlArrayExpression or OqlObjectExpression => Construct(expression, document, group),
                OqlPathExpression path => ReadPath(path, document),
                OqlUnaryExpression unary => Unary(unary.Operator, Evaluate(unary.Operand, document, group)),
                OqlBinaryExpression binary => Binary(binary, document, group),
                OqlInExpression membership => In(membership, document, group),
                OqlComprehensionExpression comprehension => Comprehend(comprehension, document, group),
                OqlQuantifiedExpression quantified => Quantify(quantified, document, group),
                OqlBetweenExpression between => Between(between, document, group),
                OqlLikeExpression like => Like(like, document, group),
                OqlCaseExpression conditional => Case(conditional, document, group),
                OqlCallExpression call => Call(call, document, group),
                _ => throw new DocumentPlanningException("CD05001", ErrorMessages.ExpressionEvaluatorEvaluateFailure),
            };
        }
        catch (DocumentPlanningException exception) when (exception.Diagnostics[0].Start is null && expression.Location is not null)
        {
            throw exception.At(expression.Location);
        }
        catch (OverflowException exception)
        {
            throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, expression.Location, exception);
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

    internal DocumentAggregateMemory Memory => (_state ??= new EvaluationState(null)).GetMemory(_cancellationToken);

    // Check every reference and original JSON number before planning, including skipped
    // Boolean branches and expressions over empty inputs.
    internal void ValidateInputs(OqlExpression? expression)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case OqlSubqueryExpression subquery:
                ValidateInputs(subquery.Query);
                break;
            case OqlComprehensionExpression or OqlQuantifiedExpression or OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression:
                foreach (var child in DocumentPlanner.PredicateChildren(expression)) { ValidateInputs(child); }
                break;
            case OqlLiteralExpression { Value: JsonElement } literal:
                _ = Evaluate(literal, default);
                break;
            case OqlParameterExpression parameter:
                if (_parameters is null || !_parameters.TryGetValue(ParameterBindings.BareName(parameter.Name), out var value))
                {
                    throw new DocumentPlanningException("CD01005", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EvaluatorValidateInputsFailure, parameter.Name), parameter.Location);
                }
                if (value is JsonElement) { _ = Evaluate(parameter, default); }
                break;
            case OqlDeleteExpression delete:
                ValidateInputs(delete.Predicate);
                ValidateInputs(delete.Returning);
                break;
            case OqlUpdateExpression update:
                foreach (var assignment in update.Assignments) { ValidateInputs(assignment.Value); }
                ValidateInputs(update.Predicate);
                ValidateInputs(update.Returning);
                break;
            case OqlInsertExpression insert:
                foreach (var pair in insert.Values) { ValidateInputs(pair.Key); ValidateInputs(pair.Value); }
                ValidateInputs(insert.Select);
                ValidateInputs(insert.Returning);
                break;
            case OqlUpsertExpression upsert:
                foreach (var pair in upsert.Values) { ValidateInputs(pair.Key); ValidateInputs(pair.Value); }
                ValidateInputs(upsert.Select);
                ValidateInputs(upsert.Returning);
                break;
            case OqlReturningExpression returning:
                foreach (var projection in returning.Projections) { ValidateInputs(projection.Expression); }
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
                foreach (var binding in select.Unnest) { ValidateInputs(binding.Collection); }
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

    internal object? Parameter(string name)
    {
        name = ParameterBindings.BareName(name);
        if (_normalizedParameters.TryGetValue(name, out var value)) { return value; }
        value = Normalize(ParameterValue(name));
        _normalizedParameters.Add(name, value);
        return value;
    }

    private object Construct(OqlExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var constructors = (_state ??= new EvaluationState(null)).Constructors ??= new();
        var key = (expression, _constructorMaxDepth);
        bool known = constructors.TryGetValue(key, out var cached);
        if (known && cached.Constant) { return cached.Value!; }
        bool constant = known ? cached.Constant : DocumentPlanner.IsConstant(expression);
        object value = expression is OqlArrayExpression array
            ? DocumentJsonValue.Array(array.Elements.Select(element => Evaluate(element, document, group)), _writeVerb, _constructorMaxDepth, _resultClause)
            : DocumentJsonValue.Object(((OqlObjectExpression)expression).Members.Select(member =>
                new KeyValuePair<string, object?>(member.Key, Evaluate(member.Value, document, group))), _writeVerb, _constructorMaxDepth, _resultClause);
        if (!known) { constructors.Add(key, (constant, constant ? value : null)); }
        return value;
    }

    private object? ParameterValue(string name)
    {
        if (_parameters is null || !_parameters.TryGetValue(ParameterBindings.BareName(name), out var value))
        {
            throw new DocumentPlanningException("CD01005", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EvaluatorValidateInputsFailure, name));
        }
        return value;
    }

    private object? Star(DocumentEvaluationContext document)
    {
        if (_starSourceName is null) { return ReadJson(document.Content); }
        var members = new List<KeyValuePair<string, object?>>
        { new(_starSourceName, ReadJson(document.Content)) };
        // Preserve source order and the object constructor's last non-MISSING rule.
        var frames = new Stack<DocumentVariableBinding>();
        for (var frame = document.Bindings; frame is not null; frame = frame.Parent)
        {
            frames.Push(frame);
        }
        foreach (var frame in frames) { members.Add(new(frame.Name, frame.Value)); }
        return DocumentJsonValue.Object(members, _writeVerb, _constructorMaxDepth);
    }

    private object? Comprehend(OqlComprehensionExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var value = Evaluate(expression.Collection, document, group);
        if (!TryCollection(value, out var array, out var unknown)) { return unknown; }
        if (expression.IsFirst)
        {
            foreach (var item in array.EnumerateArray())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var inner = document.Bind(expression.Variable, ReadJson(item));
                if (Matches(expression.Predicate, inner, group)) { return Evaluate(expression.Projection, inner, group); }
            }
            return DocumentMissing.Instance;
        }
        return DocumentJsonValue.Array(Map(), _writeVerb, _constructorMaxDepth, _resultClause);

        IEnumerable<object?> Map()
        {
            foreach (var item in array.EnumerateArray())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var inner = document.Bind(expression.Variable, ReadJson(item));
                if (!Matches(expression.Predicate, inner, group)) { continue; }
                var mapped = Evaluate(expression.Projection, inner, group);
                if (KeepMapped(expression, mapped)) { yield return mapped; }
            }
        }
    }

    private static bool TryCollection(object? value, out JsonElement array, out object? unknown)
    {
        array = value is JsonElement { ValueKind: JsonValueKind.Array } json ? json : default;
        unknown = value is DocumentMissing ? value : null;
        return array.ValueKind == JsonValueKind.Array;
    }

    private static bool? QuantifiedResult(OqlQuantifiedExpression expression, object? value)
        => (Truth(value) is true) != expression.IsEvery ? !expression.IsEvery : null;

    private static bool KeepMapped(OqlComprehensionExpression expression, object? value)
        => expression.IsFirst || value is not DocumentMissing;

    private object? Quantify(OqlQuantifiedExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var value = Evaluate(expression.Collection, document, group);
        if (!TryCollection(value, out var array, out var unknown)) { return unknown; }
        // Couchbase expression/coll_any.go and coll_every.go test sv.Truth(), so
        // NULL/MISSING element results are not true; only the collection propagates them.
        foreach (var item in array.EnumerateArray())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (QuantifiedResult(expression, Evaluate(expression.Predicate, document.Bind(expression.Variable, ReadJson(item)), group)) is bool result) { return result; }
        }
        return expression.IsEvery;
    }

    private object? ReadPath(OqlPathExpression path, DocumentEvaluationContext document)
    {
        int start = path.Segments.Count > 0 && _alias is not null &&
            string.Equals(path.Segments[0].Name, _alias, StringComparison.Ordinal) ? 1 : 0;
        var current = document.Content;
        if (document.Bindings is not null && path.Segments.Count > 0)
        {
            for (var frame = document.Bindings; frame is not null; frame = frame.Parent)
            {
                if (frame.Name != path.Segments[0].Name) { continue; }
                start = 1;
                if (frame.Value is not JsonElement json)
                { return start == path.Segments.Count ? frame.Value : DocumentMissing.Instance; }
                current = json;
                break;
            }
        }
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
        return ReadJson(current);
    }

    internal static object? Normalize(object? value)
    {
        try
        {
            return NormalizeCore(value);
        }
        catch (OverflowException exception)
        {
            throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, innerException: exception);
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
        _ => throw new DocumentPlanningException("CD02008", ErrorMessages.EvaluatorNormalizeCoreFailure),
    };

    private static object? NormalizeJson(JsonElement json)
    {
        ValidateNumbers(json);
        return ReadJson(json);
    }

    // Stored values are validated on write; input JSON is validated before planning.
    // Navigating a row must not rewalk an entire array for every expanded element.
    private static object? ReadJson(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => DocumentMissing.Instance,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.GetDecimal(),
        _ => json,
    };

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
                        throw new DocumentPlanningException("CD02007", ErrorMessages.NumberOutsideDecimalDomain, innerException: exception);
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
        if (ShortCircuitBinary(expression.Operator, ref left)) { return left is true; }
        return ApplyBinary(expression.Operator, left, Evaluate(expression.Right, document, group));
    }

    private static bool ShortCircuitBinary(string operation, ref object? left)
    {
        if (operation is "AND" or "OR") { left = Truth(left); }
        return operation == "AND" && left is false || operation == "OR" && left is true;
    }

    private object? ApplyBinary(string operation, object? left, object? right)
    {
        if (operation is "AND" or "OR") { right = Truth(right); }
        if (operation == "AND")
        {
            if (right is false) { return false; }
            if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
            return left is true && right is true ? true : null;
        }
        if (operation == "OR")
        {
            if (right is true) { return true; }
            if (left is null || right is null) { return null; }
            if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
            return left is false && right is false ? false : null;
        }
        if (operation is "IS DISTINCT FROM" or "IS NOT DISTINCT FROM")
        {
            // Couchbase expression/func_distinct.go:51-73,134-156: the top-level
            // unknown cases are definite; otherwise consume SQL++ value equality.
            object? equal = left is DocumentMissing || right is DocumentMissing ? left is DocumentMissing && right is DocumentMissing :
                left is null || right is null ? left is null && right is null : Equal(left, right);
            return operation == "IS NOT DISTINCT FROM" ? equal : equal is not true;
        }
        if (left is DocumentMissing || right is DocumentMissing) { return DocumentMissing.Instance; }
        if (left is null || right is null) { return null; }
        return operation switch
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
            "/" => Number(right) == 0 ? throw new DocumentPlanningException("CD02006", ErrorMessages.ExpressionEvaluatorBinaryFailure) : Number(left) / Number(right),
            "%" => Number(right) == 0 ? throw new DocumentPlanningException("CD02006", ErrorMessages.ExpressionEvaluatorBinaryFailure2) : Number(left) % Number(right),
            _ => throw new DocumentPlanningException("CD05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionEvaluatorBinaryFailure3, operation)),
        };
    }

    private static object? Unary(string operation, object? value) => operation switch
    {
        "EXISTS" => value is DocumentMissing ? value : value is JsonElement { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() > 0 : null,
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
        _ => throw new DocumentPlanningException("CD05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionEvaluatorBinaryFailure3, operation)),
    };

    private object? Aggregate(OqlCallExpression call, DocumentAggregateFunction function, IReadOnlyList<DocumentEvaluationContext> group)
    {
        string name = function.Name.ToString().ToUpperInvariant();
        var memory = Memory;
        bool star = call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression;
        var seen = DistinctValues(call);
        long before = memory.Bytes;
        object? result;
        try
        {
            var accumulator = CreateAggregate(function, name, memory, seen is not null);
            foreach (var document in group)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var values = star ? Array.Empty<object?>() : call.Arguments.Select(argument => Evaluate(argument, document)).ToArray();
                AddAggregate(function, name, accumulator, seen, values, memory);
            }
            result = RetainAggregateResult(name, accumulator.Finish().Value, memory);
        }
        finally { memory.Release(memory.Bytes - before); }
        // The independent JSON result outlives the input list, until the enclosing
        // expression transfers its output or discards its scratch values.
        return RetainAggregateResult(name, result, memory);
    }

    private void AddAggregate(DocumentAggregateFunction function, string name, DocumentAggregateAccumulator accumulator,
        SortedSet<object?>? seen, object?[] values, DocumentAggregateMemory memory)
    {
        if (seen is not null)
        {
            object? value = values[0];
            // ARRAY_AGG admits NULL. All other native aggregates ignore both
            // unknowns; custom aggregates retain their declared unknown policy.
            if (function.NullBehavior == DocumentNullBehavior.PropagateMissingOrNull && value is null or DocumentMissing ||
                function is DocumentBuiltinAggregateFunction && (name == "ARRAY_AGG" && value is DocumentMissing ||
                name is "STDDEV" or "VARIANCE" && value is not (decimal or long) ||
                name is "BOOL_AND" or "BOOL_OR" or "EVERY" && value is not bool))
            { return; }
            if (value is not (null or DocumentMissing) && !function.AcceptsArgument(0, FunctionKind(value))) { return; }
            if (seen.Contains(value)) { return; }
            memory!.Retain(value);
            seen.Add(value);
        }
        var context = new DocumentFunctionContext(_databaseName, _cancellationToken, _transactionStart);
        var arguments = new DocumentArguments(values, in context);
        accumulator.Add(in arguments);
    }

    private SortedSet<object?>? DistinctValues(OqlCallExpression call)
        => call.Quantifier == OqlAggregateQuantifier.Distinct
            ? new(Comparer<object?>.Create((left, right) => Compare(left, right, _cancellationToken))) : null;

    private DocumentAggregateAccumulator CreateAggregate(DocumentAggregateFunction function, string name,
        DocumentAggregateMemory memory, bool distinct)
    {
        // Native transition state is bounded independently of the input count.
        memory.Charge(name is "STDDEV" or "VARIANCE" ? 768 : 256);
        var context = new DocumentFunctionContext(_databaseName, _cancellationToken, _transactionStart);
        var accumulator = function is DocumentBuiltinAggregateFunction native
            ? native.CreateAccumulator(in context, memory, _writeVerb, _constructorMaxDepth, _resultClause)
            : function.CreateAccumulator(in context);
        if (distinct) { memory.Charge(128); }
        return accumulator;
    }

    private static object? RetainAggregateResult(string name, object? result, DocumentAggregateMemory memory)
    {
        if (name == "ARRAY_AGG" && result is not null) { memory.Retain(result); }
        return result;
    }

    internal static decimal Number(object value) => value switch { decimal number => number, long number => number,
        _ => throw new DocumentPlanningException("CD02008", ErrorMessages.ExpressionEvaluatorNumberFailure) };
    private static bool SameScalarKind(object left, object right) => Kind(left) == Kind(right) && Kind(left) is >= 1 and <= 3;

    // Total order for grouping and sorting. Predicates only compare ranges within
    // a scalar kind; ordering can still deterministically place mixed shapes.
    internal static int Compare(object? left, object? right, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        left = left is JsonElement leftJson ? ReadJson(leftJson) : Normalize(left);
        right = right is JsonElement rightJson ? ReadJson(rightJson) : Normalize(right);
        int kindOrder = Kind(left).CompareTo(Kind(right));
        if (kindOrder != 0) { return kindOrder; }
        return (left, right) switch
        {
            (null, null) => 0,
            (bool a, bool b) => a.CompareTo(b),
            (decimal a, decimal b) => a.CompareTo(b),
            (string a, string b) => StringComparer.Ordinal.Compare(a, b),
            (JsonElement a, JsonElement b) => CompareComposite(a, b, cancellationToken),
            _ => 0,
        };
    }

    // FIRST/LAST moves the nullish block. Direction still reverses MISSING vs NULL,
    // matching Couchbase orderby.adoc:85-99 and the owner's 2026-10-25 decision.
    internal static int CompareOrdered(object? left, object? right, OqlOrdering order)
    {
        if (order.NullPlacement != OqlNullPlacement.Default)
        {
            static bool Nullish(object? value) => value is null or DocumentMissing or
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
            bool leftNullish = Nullish(left), rightNullish = Nullish(right);
            if (leftNullish != rightNullish)
            { return leftNullish == (order.NullPlacement == OqlNullPlacement.First) ? -1 : 1; }
        }
        int comparison = Compare(left, right);
        return order.Descending ? -comparison : comparison;
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
        _ => throw new DocumentPlanningException("CD02008", ErrorMessages.ExpressionEvaluatorKindFailure),
    };

    private static int CompareComposite(JsonElement left, JsonElement right, CancellationToken cancellationToken)
    {
        if (left.ValueKind == JsonValueKind.Array)
        {
            var rightItems = right.EnumerateArray();
            foreach (var item in left.EnumerateArray())
            {
                if (!rightItems.MoveNext()) { return 1; }
                int comparison = Compare(item, rightItems.Current, cancellationToken);
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
            int valueComparison = Compare(leftProperties[i].Value, rightProperties[i].Value, cancellationToken);
            if (valueComparison != 0) { return valueComparison; }
        }
        return leftProperties.Length.CompareTo(rightProperties.Length);
    }

    internal sealed class ValueComparer : IComparer<object?>
    {
        internal static ValueComparer Instance { get; } = new();
        public int Compare(object? left, object? right) => DocumentExpressionEvaluator.Compare(left, right);
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentExpressionEvaluator value)
        {
            _ = value;
        }
    }
}
