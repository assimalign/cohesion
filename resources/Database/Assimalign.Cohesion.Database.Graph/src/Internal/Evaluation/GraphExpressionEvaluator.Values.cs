using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Assimalign.Cohesion.Database.Graph.Language;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphExpressionEvaluator
{
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
            _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.CaseInvalid),
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
            _ => throw new GraphPlanningException("COHDBG013", ErrorMessages.CompositeRequired, expression.Location),
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
        throw new GraphPlanningException("COHDBG013", ErrorMessages.CompositeRequired, subscript.Location);
    }
}
