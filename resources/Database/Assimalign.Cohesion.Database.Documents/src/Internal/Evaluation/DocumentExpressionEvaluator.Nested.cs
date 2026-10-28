using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    internal async ValueTask<object?> EvaluateAsync(OqlExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return expression switch
            {
                OqlSubqueryExpression subquery => await nested.EvaluateAsync(subquery, document, _cancellationToken).ConfigureAwait(false),
                OqlLiteralExpression literal => Normalize(literal.Value),
                OqlMissingExpression => DocumentMissing.Instance,
                OqlParameterExpression parameter => Parameter(parameter.Name),
                OqlStarExpression => Star(document),
                OqlMetaExpression meta => meta.Member == "id" ? document.Id : (decimal)document.Version,
                OqlArrayExpression or OqlObjectExpression => (await ConstructAsync(expression, document, group, nested).ConfigureAwait(false)),
                OqlPathExpression path => ReadPath(path, document),
                OqlUnaryExpression unary => Unary(unary.Operator, (await EvaluateAsync(unary.Operand, document, group, nested).ConfigureAwait(false))),
                OqlBinaryExpression binary => (await BinaryAsync(binary, document, group, nested).ConfigureAwait(false)),
                OqlInExpression membership => (await InAsync(membership, document, group, nested).ConfigureAwait(false)),
                OqlComprehensionExpression comprehension => (await ComprehendAsync(comprehension, document, group, nested).ConfigureAwait(false)),
                OqlQuantifiedExpression quantified => (await QuantifyAsync(quantified, document, group, nested).ConfigureAwait(false)),
                OqlBetweenExpression between => (await BetweenAsync(between, document, group, nested).ConfigureAwait(false)),
                OqlLikeExpression like => (await LikeAsync(like, document, group, nested).ConfigureAwait(false)),
                OqlCaseExpression conditional => (await CaseAsync(conditional, document, group, nested).ConfigureAwait(false)),
                OqlCallExpression call => (await CallAsync(call, document, group, nested).ConfigureAwait(false)),
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

    internal async ValueTask<object?> EvaluateProjectionAsync(OqlExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, bool bareValue, DocumentNestedExecutionContext nested)
    {
        // Named projection frames add one object level around the constructed value.
        _constructorMaxDepth = bareValue ? 256 : 255;
        try { return (await EvaluateAsync(expression, document, group, nested).ConfigureAwait(false)); }
        finally { _constructorMaxDepth = 256; }
    }

    internal async ValueTask<bool> MatchesAsync(OqlExpression? expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
        => expression is null || Truth(await EvaluateAsync(expression, document, group, nested).ConfigureAwait(false)) is true;

    private async ValueTask<object?> BinaryAsync(OqlBinaryExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var left = (await EvaluateAsync(expression.Left, document, group, nested).ConfigureAwait(false));
        if (expression.Operator is "AND" or "OR") { left = Truth(left); }
        if (expression.Operator == "AND" && left is false) { return false; }
        if (expression.Operator == "OR" && left is true) { return true; }
        var right = (await EvaluateAsync(expression.Right, document, group, nested).ConfigureAwait(false));
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
            "/" => Number(right) == 0 ? throw new DocumentPlanningException("CD02006", ErrorMessages.ExpressionEvaluatorBinaryFailure) : Number(left) / Number(right),
            "%" => Number(right) == 0 ? throw new DocumentPlanningException("CD02006", ErrorMessages.ExpressionEvaluatorBinaryFailure2) : Number(left) % Number(right),
            _ => throw new DocumentPlanningException("CD05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExpressionEvaluatorBinaryFailure3, expression.Operator)),
        };
    }

    private async ValueTask<object?> InAsync(OqlInExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var operand = (await EvaluateAsync(expression.Operand, document, group, nested).ConfigureAwait(false));
        bool missing = false;
        bool nullItem = false;
        bool matched = false;
        if (expression.Array is { } collection)
        {
            var value = (await EvaluateAsync(collection, document, group, nested).ConfigureAwait(false));
            if (operand is DocumentMissing || value is DocumentMissing) { return DocumentMissing.Instance; }
            if (value is not JsonElement { ValueKind: JsonValueKind.Array } array) { return null; }
            // Keep #1127's NULL-left rule, including an empty array.
            if (operand is null) { return null; }
            foreach (var item in array.EnumerateArray())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var itemValue = ReadJson(item);
                var equal = Equal(operand, itemValue);
                if (equal is true) { return !expression.Negated; }
                if (itemValue is null) { nullItem = true; }
            }
            return nullItem ? null : expression.Negated;
        }
        // Couchbase expression/coll_in.go:58-159: a match wins, then NULL, then MISSING.
        foreach (var item in expression.Values)
        {
            var value = (await EvaluateAsync(item, document, group, nested).ConfigureAwait(false));
            if (value is DocumentMissing) { missing = true; }
            else if (value is null) { nullItem = true; }
            else if (operand is not null and not DocumentMissing && Equal(operand, value) is true) { matched = true; }
        }
        if (operand is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is null) { return null; }
        if (matched) { return !expression.Negated; }
        if (nullItem) { return null; }
        if (missing) { return DocumentMissing.Instance; }
        return expression.Negated;
    }

    private async ValueTask<object?> BetweenAsync(OqlBetweenExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var operand = (await EvaluateAsync(expression.Operand, document, group, nested).ConfigureAwait(false));
        var lower = (await EvaluateAsync(expression.Lower, document, group, nested).ConfigureAwait(false));
        var upper = (await EvaluateAsync(expression.Upper, document, group, nested).ConfigureAwait(false));
        // Couchbase expression/comp_between.go:54-72 checks both unknown comparisons
        // before returning a Boolean, even if one ordinary bound would exclude the value.
        if (operand is DocumentMissing || lower is DocumentMissing || upper is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is null || lower is null || upper is null) { return null; }
        bool matches = SameScalarKind(operand, lower) && SameScalarKind(operand, upper) &&
            Compare(operand, lower) >= 0 && Compare(operand, upper) <= 0;
        return expression.Negated ? !matches : matches;
    }

    private async ValueTask<object?> CaseAsync(OqlCaseExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var operand = expression.Operand is null ? null : (await EvaluateAsync(expression.Operand, document, group, nested).ConfigureAwait(false));
        foreach (var branch in expression.Branches)
        {
            var condition = (await EvaluateAsync(branch.Key, document, group, nested).ConfigureAwait(false));
            bool matches = expression.Operand is null ? Truth(condition) is true :
                operand is not null and not DocumentMissing && condition is not null and not DocumentMissing && Equal(operand, condition) is true;
            if (matches) { return (await EvaluateAsync(branch.Value, document, group, nested).ConfigureAwait(false)); }
        }
        // Couchbase expression/case_simple.go:83-108 and case_searched.go:73-98:
        // unknown conditions fall through; an omitted ELSE yields NULL.
        return expression.Otherwise is null ? null : (await EvaluateAsync(expression.Otherwise, document, group, nested).ConfigureAwait(false));
    }

    private async ValueTask<object?> LikeAsync(OqlLikeExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var operand = (await EvaluateAsync(expression.Operand, document, group, nested).ConfigureAwait(false));
        if (!_likePatterns.TryGetValue(expression, out var tokens))
        {
            var pattern = (await EvaluateAsync(expression.Pattern, document, group, nested).ConfigureAwait(false));
            var escape = expression.Escape is null ? "\\" : (await EvaluateAsync(expression.Escape, document, group, nested).ConfigureAwait(false));
            // Couchbase expression/comp_like.go:105-109: MISSING precedes non-string/NULL.
            if (operand is DocumentMissing || pattern is DocumentMissing || escape is DocumentMissing) { return DocumentMissing.Instance; }
            if (operand is not string || pattern is not string text || escape is not string escapeText) { return null; }
            int escapeScalar = -1;
            foreach (var rune in escapeText.EnumerateRunes())
            {
                if (escapeScalar >= 0)
                {
                    throw new DocumentPlanningException("CD02013", ErrorMessages.LikeEscapeInvalid, expression.Escape?.Location);
                }
                escapeScalar = rune.Value;
            }
            tokens = CompileLike(text, escapeScalar);
            if (DocumentPlanner.IsConstant(expression.Pattern) && (expression.Escape is null || DocumentPlanner.IsConstant(expression.Escape)))
            {
                _likePatterns.Add(expression, tokens);
            }
        }
        if (operand is DocumentMissing) { return DocumentMissing.Instance; }
        if (operand is not string input) { return null; }
        bool matches = MatchLike(input, tokens);
        return expression.Negated ? !matches : matches;
    }

    private async ValueTask<object?> QuantifyAsync(OqlQuantifiedExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var value = (await EvaluateAsync(expression.Collection, document, group, nested).ConfigureAwait(false));
        if (value is DocumentMissing) { return value; }
        if (value is not JsonElement { ValueKind: JsonValueKind.Array } array) { return null; }
        // Couchbase expression/coll_any.go and coll_every.go test sv.Truth(), so
        // NULL/MISSING element results are not true; only the collection propagates them.
        foreach (var item in array.EnumerateArray())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            bool truth = Truth((await EvaluateAsync(expression.Predicate, document.Bind(expression.Variable, ReadJson(item)), group, nested).ConfigureAwait(false))) is true;
            if (truth != expression.IsEvery) { return !expression.IsEvery; }
        }
        return expression.IsEvery;
    }

    private async ValueTask<object?> CallAsync(OqlCallExpression call, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        // These SQL++ grammar forms retain lazy argument evaluation, like SQL CASE.
        if (call.Name is "COALESCE" or "IFMISSING" or "IFNULL" or "IFMISSINGORNULL")
        {
            foreach (var argument in call.Arguments)
            {
                var value = (await EvaluateAsync(argument, document, group, nested).ConfigureAwait(false));
                bool skip = call.Name switch
                {
                    "IFMISSING" => value is DocumentMissing,
                    "IFNULL" => value is null,
                    _ => value is null or DocumentMissing,
                };
                if (!skip) { return value; }
            }
            return null;
        }
        var function = ResolveFunction(call);
        if (function is DocumentAggregateFunction aggregate)
        {
            return (await AggregateAsync(call, aggregate, group ?? throw new DocumentPlanningException("CD05001", ErrorMessages.AggregateContextMissing), nested).ConfigureAwait(false));
        }
        var values = (await EvaluateArgumentsAsync(call.Arguments, document, group, nested).ConfigureAwait(false));
        var context = new DocumentFunctionContext(_databaseName, _cancellationToken, _transactionStart);
        var arguments = new DocumentArguments(values, in context);
        return ((DocumentScalarFunction)function).Invoke(in arguments).Value;
    }

    private async ValueTask<object?> AggregateAsync(OqlCallExpression call, DocumentAggregateFunction function, IReadOnlyList<DocumentEvaluationContext> group, DocumentNestedExecutionContext nested)
    {

        string name = function.Name.ToString().ToUpperInvariant();
        var memory = Memory;
        bool star = call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression;
        SortedSet<object?>? seen = call.Quantifier == OqlAggregateQuantifier.Distinct
            ? new(Comparer<object?>.Create((left, right) => Compare(left, right, _cancellationToken))) : null;
        long before = memory.Bytes - nested.RetainedBytes;
        object? result;
        try
        {
            // All calls own bounded native transition state. Statistics keep exact
            // moments of at most 604 bits, independent of the number of inputs.
            memory.Charge(name is "STDDEV" or "VARIANCE" ? 768 : 256);
            var initialContext = new DocumentFunctionContext(_databaseName, _cancellationToken, _transactionStart);
            var accumulator = function is DocumentBuiltinAggregateFunction native
                ? native.CreateAccumulator(in initialContext, memory, _writeVerb, _constructorMaxDepth, _resultClause)
                : function.CreateAccumulator(in initialContext);
            if (seen is not null) { memory!.Charge(128); }
            foreach (var document in group)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var values = star ? Array.Empty<object?>() : (await EvaluateArgumentsAsync(call.Arguments, document, null, nested).ConfigureAwait(false));
                if (seen is not null)
                {
                    object? value = values[0];
                    // ARRAY_AGG admits NULL. All other native aggregates ignore both
                    // unknowns; custom aggregates retain their declared unknown policy.
                    if (function.NullBehavior == DocumentNullBehavior.PropagateMissingOrNull && value is null or DocumentMissing ||
                        function is DocumentBuiltinAggregateFunction && (name == "ARRAY_AGG" && value is DocumentMissing ||
                        name is "STDDEV" or "VARIANCE" && value is not (decimal or long) ||
                        name is "BOOL_AND" or "BOOL_OR" or "EVERY" && value is not bool))
                    { continue; }
                    if (value is not (null or DocumentMissing) && !function.AcceptsArgument(0, FunctionKind(value))) { continue; }
                    if (seen.Contains(value)) { continue; }
                    memory!.Retain(value);
                    seen.Add(value);
                }
                var context = new DocumentFunctionContext(_databaseName, _cancellationToken, _transactionStart);
                var arguments = new DocumentArguments(values, in context);
                accumulator.Add(in arguments);
            }
            result = accumulator.Finish().Value;
            if (name == "ARRAY_AGG" && result is not null) { memory!.Retain(result); }
        }
        finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
        // The independent JSON result outlives the input list, until the enclosing
        // expression transfers its output or discards its scratch values.
        if (name == "ARRAY_AGG" && result is not null) { memory!.Retain(result); }
        return result;
    }
    private async ValueTask<object?[]> EvaluateArgumentsAsync(IReadOnlyList<OqlExpression> expressions,
        DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var values = new object?[expressions.Count];
        for (int index = 0; index < values.Length; index++)
        { values[index] = await EvaluateAsync(expressions[index], document, group, nested).ConfigureAwait(false); }
        return values;
    }

    private async ValueTask<object> ConstructAsync(OqlExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        if (expression is OqlArrayExpression array)
        { return DocumentJsonValue.Array(await EvaluateArgumentsAsync(array.Elements, document, group, nested).ConfigureAwait(false), _writeVerb, _constructorMaxDepth, _resultClause); }
        var members = new List<KeyValuePair<string, object?>>();
        foreach (var member in ((OqlObjectExpression)expression).Members)
        { members.Add(new(member.Key, await EvaluateAsync(member.Value, document, group, nested).ConfigureAwait(false))); }
        return DocumentJsonValue.Object(members, _writeVerb, _constructorMaxDepth, _resultClause);
    }

    private async ValueTask<object?> ComprehendAsync(OqlComprehensionExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var value = await EvaluateAsync(expression.Collection, document, group, nested).ConfigureAwait(false);
        if (value is DocumentMissing) { return value; }
        if (value is not JsonElement { ValueKind: JsonValueKind.Array } array) { return null; }
        using var scratch = Memory.Reserve();
        var values = new List<object?>();
        foreach (var item in array.EnumerateArray())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var inner = document.Bind(expression.Variable, ReadJson(item));
            if (!await MatchesAsync(expression.Predicate, inner, group, nested).ConfigureAwait(false)) { continue; }
            var mapped = await EvaluateAsync(expression.Projection, inner, group, nested).ConfigureAwait(false);
            if (expression.IsFirst) { return mapped; }
            if (mapped is DocumentMissing) { continue; }
            scratch.Retain(mapped);
            values.Add(mapped);
        }
        return expression.IsFirst ? DocumentMissing.Instance : DocumentJsonValue.Array(values, _writeVerb, _constructorMaxDepth, _resultClause);
    }
}
