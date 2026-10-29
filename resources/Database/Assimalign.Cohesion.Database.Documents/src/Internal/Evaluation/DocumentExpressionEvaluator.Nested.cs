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
            nested.ConsumeWork(_cancellationToken);
            return expression switch
            {
                OqlSubqueryExpression subquery => await nested.EvaluateAsync(subquery, CaptureBindings(document, nested.ProjectionBindings, nested.ProjectionBase), _cancellationToken).ConfigureAwait(false),
                OqlLiteralExpression literal => Normalize(literal.Value),
                OqlMissingExpression => DocumentMissing.Instance,
                OqlParameterExpression parameter => Parameter(parameter.Name),
                OqlStarExpression => Star(document),
                OqlMetaExpression meta => ReadMetadata(meta, document),
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
        // Use the same serialized row shape as the synchronous projection path.
        _constructorMaxDepth = bareValue ? 256 : 255;
        try
        {
            var value = await EvaluateAsync(expression, document, group, nested).ConfigureAwait(false);
            // Subqueries and callbacks can return JSON without using a constructor
            // on this evaluator. Include the named wrapper before exposing any cell.
            DocumentJsonValue.ValidateDepth(value, _constructorMaxDepth, resultClause: _resultClause, cancellationToken: _cancellationToken);
            return value;
        }
        catch (DocumentPlanningException exception) when (exception.Diagnostics[0].Start is null && expression.Location is not null)
        { throw exception.At(expression.Location); }
        finally { _constructorMaxDepth = 256; }
    }

    internal async ValueTask<bool> MatchesAsync(OqlExpression? expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        long before = Memory.Bytes - nested.RetainedBytes;
        try { return expression is null || Truth(await EvaluateAsync(expression, document, group, nested).ConfigureAwait(false)) is true; }
        finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); }
    }

    private async ValueTask<object?> BinaryAsync(OqlBinaryExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var left = await EvaluateAsync(expression.Left, document, group, nested).ConfigureAwait(false);
        if (ShortCircuitBinary(expression.Operator, ref left)) { return left is true; }
        return ApplyBinary(expression.Operator, left, await EvaluateAsync(expression.Right, document, group, nested).ConfigureAwait(false));
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
            return InArray(operand, value, expression.Negated);
        }
        // Couchbase expression/coll_in.go:58-159: a match wins, then NULL, then MISSING.
        foreach (var item in expression.Values)
        {
            long before = Memory.Bytes - nested.RetainedBytes;
            try
            {
                var value = await EvaluateAsync(item, document, group, nested).ConfigureAwait(false);
                AddInItem(operand, value, ref missing, ref nullItem, ref matched);
            }
            finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); }
        }
        return FinishIn(expression.Negated, operand, missing, nullItem, matched);
    }

    private async ValueTask<object?> BetweenAsync(OqlBetweenExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var operand = (await EvaluateAsync(expression.Operand, document, group, nested).ConfigureAwait(false));
        var lower = (await EvaluateAsync(expression.Lower, document, group, nested).ConfigureAwait(false));
        var upper = (await EvaluateAsync(expression.Upper, document, group, nested).ConfigureAwait(false));
        return ApplyBetween(expression, operand, lower, upper);
    }

    private async ValueTask<object?> CaseAsync(OqlCaseExpression expression, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var operand = expression.Operand is null ? null : (await EvaluateAsync(expression.Operand, document, group, nested).ConfigureAwait(false));
        foreach (var branch in expression.Branches)
        {
            bool matches;
            long before = Memory.Bytes - nested.RetainedBytes;
            try
            {
                var condition = await EvaluateAsync(branch.Key, document, group, nested).ConfigureAwait(false);
                matches = CaseMatches(expression, operand, condition);
            }
            finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); }
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
            if (!PrepareLike(expression, operand, pattern, escape, out tokens, out var result)) { return result; }
        }
        return ApplyLike(expression, operand, tokens);
    }

    private async ValueTask<object?> QuantifyAsync(OqlQuantifiedExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var value = (await EvaluateAsync(expression.Collection, document, group, nested).ConfigureAwait(false));
        if (!TryCollection(value, out var array, out var unknown)) { return unknown; }
        // Couchbase expression/coll_any.go and coll_every.go test sv.Truth(), so
        // NULL/MISSING element results are not true; only the collection propagates them.
        foreach (var item in array.EnumerateArray())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long before = Memory.Bytes - nested.RetainedBytes;
            try
            {
                if (QuantifiedResult(expression, await EvaluateAsync(expression.Predicate, document.Bind(expression.Variable, ReadJson(item)), group, nested).ConfigureAwait(false)) is bool result) { return result; }
            }
            finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); }
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
                long before = Memory.Bytes - nested.RetainedBytes;
                bool keep = false;
                try
                {
                    var value = await EvaluateAsync(argument, document, group, nested).ConfigureAwait(false);
                    keep = !SkipConditional(call.Name, value);
                    if (keep) { return value; }
                }
                finally { if (!keep) { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); } }
            }
            return null;
        }
        var function = ResolveFunction(call);
        if (function is DocumentAggregateFunction aggregate)
        {
            return (await AggregateAsync(call, aggregate, group ?? throw new DocumentPlanningException("CD05001", ErrorMessages.AggregateContextMissing), nested).ConfigureAwait(false));
        }
        var values = (await EvaluateArgumentsAsync(call.Arguments, document, group, nested).ConfigureAwait(false));
        return InvokeScalar((DocumentScalarFunction)function, values);
    }

    private async ValueTask<object?> AggregateAsync(OqlCallExpression call, DocumentAggregateFunction function, IReadOnlyList<DocumentEvaluationContext> group, DocumentNestedExecutionContext nested)
    {

        string name = function.Name.ToString().ToUpperInvariant();
        var memory = Memory;
        bool star = call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression;
        var seen = DistinctValues(call);
        long before = memory.Bytes - nested.RetainedBytes;
        object? result;
        try
        {
            var accumulator = CreateAggregate(function, name, memory, seen is not null);
            foreach (var document in group)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                long inputStart = memory.Bytes - nested.RetainedBytes;
                object?[] values;
                try
                {
                    values = star ? Array.Empty<object?>() : await EvaluateArgumentsAsync(call.Arguments, document, null, nested).ConfigureAwait(false);
                    for (int index = 0; index < values.Length; index++)
                    { if (values[index] is JsonElement json) { values[index] = json.Clone(); } }
                }
                finally { memory.Release(memory.Bytes - nested.RetainedBytes - inputStart); }
                AddAggregate(function, name, accumulator, seen, values, memory);
            }
            result = RetainAggregateResult(name, accumulator.Finish().Value, memory);
        }
        finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
        // The independent JSON result outlives the input list, until the enclosing
        // expression transfers its output or discards its scratch values.
        return RetainAggregateResult(name, result, memory);
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
        { return await DocumentJsonValue.ArrayAsync(ElementsAsync(array), _writeVerb, _constructorMaxDepth, _resultClause).ConfigureAwait(false); }
        using var members = Memory.Reserve();
        var memberCharges = new Dictionary<string, long>(StringComparer.Ordinal);
        return await DocumentJsonValue.ObjectAsync(MembersAsync(), _writeVerb, _constructorMaxDepth, _resultClause).ConfigureAwait(false);

        async IAsyncEnumerable<object?> ElementsAsync(OqlArrayExpression array)
        {
            foreach (var element in array.Elements)
            {
                long before = Memory.Bytes - nested.RetainedBytes;
                try { yield return await EvaluateAsync(element, document, group, nested).ConfigureAwait(false); }
                finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); }
            }
        }
        async IAsyncEnumerable<KeyValuePair<string, object?>> MembersAsync()
        {
            foreach (var member in ((OqlObjectExpression)expression).Members)
            {
                long before = Memory.Bytes - nested.RetainedBytes - members.Bytes;
                try
                {
                    var value = await EvaluateAsync(member.Value, document, group, nested).ConfigureAwait(false);
                    if (value is not DocumentMissing)
                    {
                        // ObjectAsync keeps the last non-MISSING value for each key
                        // until serialization. Transfer its ownership before releasing
                        // this member's discarded expression scratch.
                        long bytes = 64L + Memory.ValueBytes(value);
                        members.Charge(bytes);
                        members.Rewind(members.Bytes - memberCharges.GetValueOrDefault(member.Key));
                        memberCharges[member.Key] = bytes;
                    }
                    yield return new(member.Key, value);
                }
                finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - members.Bytes - before); }
            }
        }
    }

    private async ValueTask<object?> ComprehendAsync(OqlComprehensionExpression expression, DocumentEvaluationContext document,
        IReadOnlyList<DocumentEvaluationContext>? group, DocumentNestedExecutionContext nested)
    {
        var value = await EvaluateAsync(expression.Collection, document, group, nested).ConfigureAwait(false);
        if (!TryCollection(value, out var array, out var unknown)) { return unknown; }
        if (!expression.IsFirst)
        { return await DocumentJsonValue.ArrayAsync(MapAsync(), _writeVerb, _constructorMaxDepth, _resultClause).ConfigureAwait(false); }
        await foreach (var mapped in MapAsync().ConfigureAwait(false)) { return mapped; }
        return DocumentMissing.Instance;

        async IAsyncEnumerable<object?> MapAsync()
        {
            foreach (var item in array.EnumerateArray())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var inner = document.Bind(expression.Variable, ReadJson(item));
                if (!await MatchesAsync(expression.Predicate, inner, group, nested).ConfigureAwait(false)) { continue; }
                long before = Memory.Bytes - nested.RetainedBytes;
                try
                {
                    var mapped = await EvaluateAsync(expression.Projection, inner, group, nested).ConfigureAwait(false);
                    if (KeepMapped(expression, mapped)) { yield return mapped; }
                }
                finally { Memory.Release(Memory.Bytes - nested.RetainedBytes - before); }
            }
        }
    }
}
