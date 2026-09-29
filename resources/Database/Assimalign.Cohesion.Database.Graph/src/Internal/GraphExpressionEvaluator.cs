using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphExpressionEvaluator
{
    internal static object? Evaluate(GqlExpression expression, IReadOnlyDictionary<string, object> bindings) => expression switch
    {
        GqlLiteralExpression literal => literal.Value,
        GqlPropertyExpression property => Property(bindings[property.Variable], property.Property),
        GqlBinaryExpression binary => Binary(binary, bindings),
        _ => throw new DatabaseException("COHDBG001: Unsupported graph expression."),
    };
    internal static object? Property(object entity, string name) => entity switch
    {
        GraphNode node => node.Properties.TryGetValue(name, out var value) ? value : null,
        GraphRelationship relationship => relationship.Properties.TryGetValue(name, out var value) ? value : null,
        _ => throw new DatabaseException("COHDBG003: Property access requires a node or relationship."),
    };
    private static object? Binary(GqlBinaryExpression binary, IReadOnlyDictionary<string, object> bindings)
    {
        var left = Evaluate(binary.Left, bindings);
        var right = Evaluate(binary.Right, bindings);
        if (binary.Operator == "AND") { return left is true && right is true; }
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
