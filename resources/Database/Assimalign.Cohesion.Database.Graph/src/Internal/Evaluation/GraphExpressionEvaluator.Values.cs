using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Assimalign.Cohesion.Database.Graph.Language;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphExpressionEvaluator
{
    private static object? Comprehension(GqlListComprehensionExpression expression, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var source = Evaluate(expression.List, bindings, context, parameters);
        if (source is null) { return null; }
        if (source is not object?[] list)
        { throw new GraphPlanningException("CG02014", ErrorMessages.ListOperationInvalid, expression.List.Location); }
        var budget = context.Operation?.QueryBudget;
        using var memory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
        var token = context.Operation?.FunctionCancellation ?? default;
        memory?.RetainValue(list, token);
        // Reserve the complete buffer before allocation. Nested evaluation shares this ledger.
        memory?.Add(128L + 16L * list.Length);
        var output = new object?[list.Length];
        var scope = new GraphIteratorBinding(bindings, expression.Variable);
        int count = 0;
        foreach (var element in list)
        {
            token.ThrowIfCancellationRequested();
            budget?.Consume();
            scope.Value = element;
            if (expression.Predicate is not null && Boolean(Evaluate(expression.Predicate, scope, context, parameters), expression.Predicate) != true)
            { continue; }
            var value = expression.Projection is null ? element : Evaluate(expression.Projection, scope, context, parameters);
            memory?.RetainValue(value, token);
            output[count++] = value;
        }
        if (count == output.Length) { return output; }
        memory?.Add(32L + 16L * count);
        return output[..count];
    }

    private static object? Reduce(GqlReduceExpression expression, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var source = Evaluate(expression.List, bindings, context, parameters);
        if (source is null) { return null; }
        if (source is not object?[] list)
        { throw new GraphPlanningException("CG02015", ErrorMessages.ReductionListRequired, expression.List.Location); }
        var budget = context.Operation?.QueryBudget;
        using var memory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
        var token = context.Operation?.FunctionCancellation ?? default;
        memory?.RetainValue(list, token);
        var accumulator = new GraphIteratorBinding(bindings, expression.Accumulator);
        var scope = new GraphIteratorBinding(accumulator, expression.Variable);
        accumulator.Value = Evaluate(expression.Initial, bindings, context, parameters);
        memory?.RetainValue(accumulator.Value, token);
        foreach (var element in list)
        {
            token.ThrowIfCancellationRequested();
            budget?.Consume();
            scope.Value = element;
            var next = Evaluate(expression.Body, scope, context, parameters);
            // Retain the replacement before releasing the old value: the two coexist, and
            // a replacement can contain or be the old value. Every failure unwinds the owner.
            memory?.RetainValue(next, token);
            memory?.ReleaseValue(accumulator.Value);
            accumulator.Value = next;
        }
        return accumulator.Value;
    }

    private static object? ListPredicate(GqlListPredicateExpression expression, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var source = Evaluate(expression.List, bindings, context, parameters);
        if (source is null) { return null; }
        if (source is not object?[] list)
        { throw new GraphPlanningException("CG02015", ErrorMessages.ReductionListRequired, expression.List.Location); }
        var budget = context.Operation?.QueryBudget;
        using var memory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
        var token = context.Operation?.FunctionCancellation ?? default;
        memory?.RetainValue(list, token);
        var scope = new GraphIteratorBinding(bindings, expression.Variable);
        bool unknown = false, matched = false;
        foreach (var element in list)
        {
            token.ThrowIfCancellationRequested();
            budget?.Consume();
            scope.Value = element;
            var value = Boolean(Evaluate(expression.Predicate, scope, context, parameters), expression.Predicate);
            if (value is null) { unknown = true; continue; }
            if (expression.Kind == GqlListPredicateKind.All && value == false) { return false; }
            if (value == true)
            {
                if (expression.Kind == GqlListPredicateKind.Any) { return true; }
                if (expression.Kind == GqlListPredicateKind.None || expression.Kind == GqlListPredicateKind.Single && matched) { return false; }
                matched = true;
            }
        }
        if (unknown) { return null; }
        return expression.Kind is GqlListPredicateKind.All or GqlListPredicateKind.None ||
            expression.Kind == GqlListPredicateKind.Single && matched;
    }

    private static object? Slice(GqlListSliceExpression expression, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var source = Evaluate(expression.Operand, bindings, context, parameters);
        var budget = context.Operation?.QueryBudget;
        using var memory = budget is null ? null : new GraphMemoryOwner(budget.Memory);
        var token = context.Operation?.FunctionCancellation ?? default;
        // Bounds can themselves evaluate comprehensions. Keep the source's grant live
        // throughout that evaluation, including when the source was a temporary result.
        if (source is object?[] retained) { memory?.RetainValue(retained, token); }
        var from = expression.From is null ? 0L : Evaluate(expression.From, bindings, context, parameters);
        var to = expression.To is null ? null : Evaluate(expression.To, bindings, context, parameters);
        if (source is null || from is null || expression.To is not null && to is null) { return null; }
        if (source is not object?[] list)
        { throw new GraphPlanningException("CG02014", ErrorMessages.ListOperationInvalid, expression.Operand.Location); }
        int start = SliceBound(from, list.Length, expression.From ?? expression);
        int end = expression.To is null ? list.Length : SliceBound(to!, list.Length, expression.To);
        int length = Math.Max(0, end - start);
        memory?.Add(32L + 16L * length);
        var output = new object?[length];
        for (int i = 0; i < length; i++)
        { token.ThrowIfCancellationRequested(); budget?.Consume(); output[i] = list[start + i]; }
        return output;
    }

    private static int SliceBound(object value, int length, GqlExpression expression)
    {
        // Neo4j CypherFunctions.asIntExact accepts finite numeric bounds, truncates toward
        // zero, then requires Int32 range. Use decimal to avoid Convert's rounding behavior.
        if (!IsNumber(value))
        { throw new GraphPlanningException("CG02014", ErrorMessages.ListOperationInvalid, expression.Location); }
        decimal bound;
        try
        {
            if (value is float or double)
            {
                double truncated = Math.Truncate(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                if (!double.IsFinite(truncated) || truncated < int.MinValue || truncated > int.MaxValue)
                { throw new GraphPlanningException("CG02014", ErrorMessages.ListOperationInvalid, expression.Location); }
                bound = (decimal)truncated;
            }
            else { bound = decimal.Truncate(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); }
        }
        catch (OverflowException error)
        { throw new GraphPlanningException("CG02014", ErrorMessages.ListOperationInvalid, expression.Location, error); }
        if (bound < int.MinValue || bound > int.MaxValue)
        { throw new GraphPlanningException("CG02014", ErrorMessages.ListOperationInvalid, expression.Location); }
        long offset = (int)bound;
        if (offset < 0) { offset += length; }
        return (int)Math.Clamp(offset, 0, length);
    }

    private static object Map(GqlMapExpression map, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var values = new Dictionary<string, object?>(map.Entries.Count, StringComparer.Ordinal);
        foreach (var entry in map.Entries) { values.Add(entry.Key, Evaluate(entry.Value, bindings, context, parameters)); }
        return new ReadOnlyDictionary<string, object?>(values);
    }

    private static object? Case(GqlCaseExpression conditional, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var operand = conditional.Operand is null ? null : Evaluate(conditional.Operand, bindings, context, parameters);
        foreach (var branch in conditional.Alternatives)
        {
            var when = Evaluate(branch.When, bindings, context, parameters);
            if ((conditional.Operand is null ? Boolean(when, branch.When) : CaseComparison(operand, when, branch.Comparison)) == true)
            { return Evaluate(branch.Then, bindings, context, parameters); }
        }
        return conditional.Otherwise is null ? null : Evaluate(conditional.Otherwise, bindings, context, parameters);
    }

    private static bool? CaseComparison(object? left, object? right, string comparison)
    {
        if (comparison == "IS NULL") { return left is null; }
        if (comparison == "IS NOT NULL") { return left is not null; }
        if (comparison is "=" or "<>" or "!=")
        { var equal = GraphCompositeValues.Equal(left, right); return comparison == "=" ? equal : !equal; }
        if (left is null || right is null) { return null; }
        int? order = Compare(left, right);
        return comparison switch
        {
            "<" => order is null ? null : order < 0, "<=" => order is null ? null : order <= 0,
            ">" => order is null ? null : order > 0, ">=" => order is null ? null : order >= 0,
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.CaseInvalid),
        };
    }

    private static object? Coalesce(GqlCoalesceExpression coalesce, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        foreach (var operand in coalesce.Operands)
        { if (Evaluate(operand, bindings, context, parameters) is { } value) { return value; } }
        return null;
    }

    private static object? NullIf(GqlNullIfExpression nullIf, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = Evaluate(nullIf.Left, bindings, context, parameters);
        var right = Evaluate(nullIf.Right, bindings, context, parameters);
        return GraphCompositeValues.Equal(left, right) == true ? null : left;
    }

    private static object? Member(object? operand, string key, GqlExpression expression)
        => operand switch
        {
            null => null,
            GraphNode or GraphRelationship => Property(operand, key),
            IReadOnlyDictionary<string, object?> map => map.TryGetValue(key, out var value) ? value : null,
            _ => throw new GraphPlanningException("CG02013", ErrorMessages.CompositeRequired, expression.Location),
        };

    private static object? Subscript(GqlSubscriptExpression subscript, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var operand = Evaluate(subscript.Operand, bindings, context, parameters);
        var index = Evaluate(subscript.Index, bindings, context, parameters);
        if (operand is null || index is null) { return null; }
        if (operand is IReadOnlyDictionary<string, object?> or GraphNode or GraphRelationship && index is string key) { return Member(operand, key, subscript); }
        if (operand is object?[] list && index is byte or sbyte or short or ushort or int or uint or long or ulong)
        {
            if (index is ulong large && large > long.MaxValue) { return null; }
            long offset = Convert.ToInt64(index, CultureInfo.InvariantCulture);
            if (offset < 0) { offset += list.Length; }
            return offset >= 0 && offset < list.Length ? list[(int)offset] : null;
        }
        throw new GraphPlanningException("CG02013", ErrorMessages.CompositeRequired, subscript.Location);
    }
}
