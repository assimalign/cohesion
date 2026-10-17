using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Evaluates scalar expressions with three-valued logic and stack-checked descent.</summary>
internal static class GraphExpressionEvaluator
{
    internal static object? Evaluate(GqlExpression expression, IReadOnlyDictionary<string, object> bindings)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        try
        {
            return expression switch
            {
                GqlLiteralExpression literal => literal.Value,
                GqlPropertyExpression property => Property(bindings[property.Variable], property.Property),
                GqlBinaryExpression binary => Binary(binary, bindings),
                GqlLogicalExpression logical => Logical(logical, bindings),
                GqlLabeledPredicate labeled => Labeled(labeled, bindings),
                GqlUnaryExpression unary => Unary(unary, bindings),
                GqlIsNullExpression test => (Evaluate(test.Operand, bindings) is null) != test.IsNegated,
                GqlTruthTestExpression test => (Boolean(Evaluate(test.Operand, bindings), test.Operand) == test.TruthValue) != test.IsNegated,
                GqlInExpression membership => In(membership, bindings),
                GqlStringPredicateExpression predicate => StringPredicate(predicate, bindings),
                _ => throw new GraphPlanningException("COHDBG001", "Unsupported graph expression."),
            };
        }
        catch (ArithmeticException error)
        {
            throw new GraphPlanningException("COHDBG013", ErrorMessages.ArithmeticFailure, expression.Location, error);
        }
    }

    // WHERE and every Boolean operator accept only Boolean or NULL; no truthiness coercion.
    internal static bool? Boolean(object? value, GqlExpression? expression = null) => value switch
    {
        null => null,
        bool boolean => boolean,
        _ => throw new GraphPlanningException("COHDBG013", ErrorMessages.BooleanRequired, expression?.Location),
    };

    private static object? Logical(GqlLogicalExpression logical, IReadOnlyDictionary<string, object> bindings)
    {
        bool unknown = false;
        bool parity = false;
        foreach (var operand in logical.Operands)
        {
            bool? value = Boolean(Evaluate(operand, bindings), operand);
            if (logical.Operator == GqlLogicalOperator.And && value == false) { return false; }
            if (logical.Operator == GqlLogicalOperator.Or && value == true) { return true; }
            unknown |= value is null;
            parity ^= value == true;
        }
        if (unknown) { return null; }
        return logical.Operator switch
        {
            GqlLogicalOperator.And => true,
            GqlLogicalOperator.Or => false,
            GqlLogicalOperator.Xor => parity,
            _ => throw new GraphPlanningException("COHDBG001", "Unsupported logical operator."),
        };
    }

    // ISO <labeled predicate>: true or false for a bound element. A variable with no binding (a
    // null optional match, once that clause exists) is UNKNOWN; AND propagates UNKNOWN unless an
    // operand is false, and WHERE keeps only true.
    private static object? Labeled(GqlLabeledPredicate labeled, IReadOnlyDictionary<string, object> bindings)
    {
        if (!bindings.TryGetValue(labeled.Variable, out var entity) || entity is null) { return null; }
        bool matches = entity switch
        {
            GraphNode node => GraphLabelEvaluator.Matches(labeled.LabelExpression, node.Labels),
            GraphRelationship relationship => GraphLabelEvaluator.Matches(labeled.LabelExpression, relationship.Type),
            _ => throw new GraphPlanningException("COHDBG003", "A labeled predicate requires a node or relationship."),
        };
        return matches != labeled.IsNegated;
    }
    internal static object? Property(object entity, string name) => entity switch
    {
        GraphNode node => node.Properties.TryGetValue(name, out var value) ? value : null,
        GraphRelationship relationship => relationship.Properties.TryGetValue(name, out var value) ? value : null,
        _ => throw new GraphPlanningException("COHDBG003", "Property access requires a node or relationship."),
    };
    private static object? Unary(GqlUnaryExpression unary, IReadOnlyDictionary<string, object> bindings)
    {
        var value = Evaluate(unary.Operand, bindings);
        if (unary.Operator == "NOT") { return !Boolean(value, unary.Operand); }
        if (value is null) { return null; }
        if (!IsNumber(value)) { throw new GraphPlanningException("COHDBG013", ErrorMessages.NumericRequired, unary.Location); }
        if (value is float or double)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Finite(unary.Operator == "-" ? -number : number);
        }
        if (value is ulong or decimal)
        {
            decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return unary.Operator == "-" ? -number : number;
        }
        long integer = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return unary.Operator == "-" ? checked(-integer) : integer;
    }

    private static object? In(GqlInExpression membership, IReadOnlyDictionary<string, object> bindings)
    {
        var value = Evaluate(membership.Operand, bindings);
        var candidates = new object?[membership.List.Items.Count];
        for (int index = 0; index < candidates.Length; index++)
        {
            candidates[index] = Evaluate(membership.List.Items[index], bindings);
        }
        bool unknown = false;
        foreach (var candidate in candidates)
        {
            if (value is null || candidate is null) { unknown = true; }
            else if (Equal(value, candidate)) { return true; }
        }
        return unknown ? null : false;
    }

    private static object? StringPredicate(GqlStringPredicateExpression predicate, IReadOnlyDictionary<string, object> bindings)
    {
        var left = Evaluate(predicate.Left, bindings);
        var right = Evaluate(predicate.Right, bindings);
        if (left is not string text || right is not string search) { return null; }
        return predicate.Operator switch
        {
            GqlStringPredicateOperator.StartsWith => text.StartsWith(search, StringComparison.Ordinal),
            GqlStringPredicateOperator.EndsWith => text.EndsWith(search, StringComparison.Ordinal),
            GqlStringPredicateOperator.Contains => text.Contains(search, StringComparison.Ordinal),
            _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.UnsupportedStringPredicate),
        };
    }

    private static object? Binary(GqlBinaryExpression binary, IReadOnlyDictionary<string, object> bindings)
    {
        var left = Evaluate(binary.Left, bindings);
        var right = Evaluate(binary.Right, bindings);
        if (left is null || right is null) { return null; }
        if (binary.Operator is "+" or "-" or "*" or "/" or "%" or "||")
        {
            if (binary.Operator is "+" or "||" && left is string a && right is string b) { return a + b; }
            if (binary.Operator == "||" || !IsNumber(left) || !IsNumber(right))
            { throw new GraphPlanningException("COHDBG013", ErrorMessages.ArithmeticOperands, binary.Location); }
            return Arithmetic(binary.Operator, left, right);
        }
        int? order = Compare(left, right);
        return binary.Operator switch
        {
            "=" => order == 0, "!=" or "<>" => order != 0,
            "<" => order is null ? null : order < 0,
            "<=" => order is null ? null : order <= 0,
            ">" => order is null ? null : order > 0,
            ">=" => order is null ? null : order >= 0,
            _ => throw new GraphPlanningException("COHDBG001", "Unsupported graph operator."),
        };
    }

    private static object Arithmetic(string op, object left, object right)
    {
        if (left is float or double || right is float or double)
        {
            double a = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            double b = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            if (op is "/" or "%" && b == 0) { throw new DivideByZeroException(ErrorMessages.ArithmeticFailure); }
            return Finite(op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => a % b });
        }
        if (left is ulong or decimal || right is ulong or decimal)
        {
            decimal a = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            decimal b = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => a % b };
        }
        long x = Convert.ToInt64(left, CultureInfo.InvariantCulture);
        long y = Convert.ToInt64(right, CultureInfo.InvariantCulture);
        // PostgreSQL src/backend/utils/adt/int8.c:584-590 handles the representable remainder
        // separately because the machine operation can overflow for Int64.MinValue % -1.
        if (op == "%" && y == -1) { return 0L; }
        checked
        {
            return op switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, _ => x % y };
        }
    }

    private static double Finite(double value) => double.IsFinite(value) ? value :
        throw new OverflowException(ErrorMessages.ArithmeticFailure);

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
