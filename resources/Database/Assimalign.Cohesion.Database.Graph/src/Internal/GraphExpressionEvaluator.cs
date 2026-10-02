using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Evaluates a validated <c>WHERE</c> predicate against one binding. An <c>AND</c> chain is one
/// n-ary node evaluated by a loop; the walk recurses only where predicates nest (parentheses and
/// comparison operands) and checks the stack before each descent, so a predicate deeper than the
/// executing thread's stack fails its statement with <c>COHDBG007</c> instead of overflowing it.
/// </summary>
internal static class GraphExpressionEvaluator
{
    /// <summary>Evaluates an expression; a predicate yields true, false or null (unknown).</summary>
    /// <param name="expression">The validated expression.</param>
    /// <param name="bindings">The binding to evaluate against.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InsufficientExecutionStackException">The expression nests deeper than the thread's stack allows.</exception>
    internal static object? Evaluate(GqlExpression expression, IReadOnlyDictionary<string, object> bindings) => expression switch
    {
        GqlLiteralExpression literal => literal.Value,
        GqlPropertyExpression property => Property(bindings[property.Variable], property.Property),
        GqlBinaryExpression binary => Binary(binary, bindings),
        GqlLogicalExpression logical => Logical(logical, bindings),
        GqlLabeledPredicate labeled => Labeled(labeled, bindings),
        _ => throw new DatabaseException("COHDBG001: Unsupported graph expression."),
    };

    // ISO three-valued AND over the whole chain, first to last: false as soon as an operand is
    // false, otherwise unknown when any operand was not true, otherwise true. WHERE keeps only true.
    private static object? Logical(GqlLogicalExpression logical, IReadOnlyDictionary<string, object> bindings)
    {
        if (logical.Operator != GqlLogicalOperator.And) { throw new DatabaseException("COHDBG001: Unsupported logical operator."); }
        RuntimeHelpers.EnsureSufficientExecutionStack();
        bool unknown = false;
        foreach (var operand in logical.Operands)
        {
            switch (Evaluate(operand, bindings))
            {
                case false:
                    return false;
                case true:
                    break;
                default:
                    unknown = true;
                    break;
            }
        }
        return unknown ? null : true;
    }

    // ISO <labeled predicate>: true or false for a bound element. A variable with no binding (a
    // null optional match, once that clause exists) is UNKNOWN, which AND and WHERE treat as false.
    private static object? Labeled(GqlLabeledPredicate labeled, IReadOnlyDictionary<string, object> bindings)
    {
        if (!bindings.TryGetValue(labeled.Variable, out var entity) || entity is null) { return null; }
        bool matches = entity switch
        {
            GraphNode node => GraphLabelEvaluator.Matches(labeled.LabelExpression, node.Labels),
            GraphRelationship relationship => GraphLabelEvaluator.Matches(labeled.LabelExpression, relationship.Type),
            _ => throw new DatabaseException("COHDBG003: A labeled predicate requires a node or relationship."),
        };
        return matches != labeled.IsNegated;
    }
    internal static object? Property(object entity, string name) => entity switch
    {
        GraphNode node => node.Properties.TryGetValue(name, out var value) ? value : null,
        GraphRelationship relationship => relationship.Properties.TryGetValue(name, out var value) ? value : null,
        _ => throw new DatabaseException("COHDBG003: Property access requires a node or relationship."),
    };
    private static object? Binary(GqlBinaryExpression binary, IReadOnlyDictionary<string, object> bindings)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var left = Evaluate(binary.Left, bindings);
        var right = Evaluate(binary.Right, bindings);
        if (left is null || right is null) { return null; }
        int? order = Compare(left, right);
        return binary.Operator switch
        {
            "=" => order == 0, "!=" or "<>" => order != 0,
            "<" => order < 0, "<=" => order <= 0, ">" => order > 0, ">=" => order >= 0,
            _ => throw new DatabaseException("COHDBG001: Unsupported graph operator."),
        };
    }
    internal static bool Equal(object? left, object? right) => left is null ? right is null : right is not null && Compare(left, right) == 0;
    private static int? Compare(object left, object right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            if (left is float or double || right is float or double)
            { return Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture)); }
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }
        return (left, right) switch
        {
            (string a, string b) => StringComparer.Ordinal.Compare(a, b),
            (bool a, bool b) => a.CompareTo(b),
            _ => left.Equals(right) ? 0 : null,
        };
    }
    private static bool IsNumber(object value) => value is sbyte or short or int or long or byte or ushort or uint or ulong or float or double or decimal;
}
