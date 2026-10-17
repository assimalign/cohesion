using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Evaluates scalar expressions with three-valued logic and stack-checked descent.</summary>
internal static partial class GraphExpressionEvaluator
{
    internal static object? Evaluate(GqlExpression expression, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        try
        {
            return expression switch
            {
                GqlLiteralExpression literal => literal.Value,
                GqlParameterExpression parameter => Parameter(parameter, parameters),
                GqlVariableExpression variable => bindings[variable.Variable],
                GqlPropertyExpression property => Property(bindings[property.Variable], property.Property),
                GqlBinaryExpression binary => Binary(binary, bindings, parameters),
                GqlLogicalExpression logical => Logical(logical, bindings, parameters),
                GqlLabeledPredicate labeled => Labeled(labeled, bindings),
                GqlUnaryExpression unary => Unary(unary, bindings, parameters),
                GqlIsNullExpression test => (Evaluate(test.Operand, bindings, parameters) is null) != test.IsNegated,
                GqlTruthTestExpression test => (Boolean(Evaluate(test.Operand, bindings, parameters), test.Operand) == test.TruthValue) != test.IsNegated,
                GqlListExpression list => list.Items.Select(item => Evaluate(item, bindings, parameters)).ToArray(),
                GqlMapExpression map => Map(map, bindings, parameters),
                GqlCaseExpression conditional => Case(conditional, bindings, parameters),
                GqlCoalesceExpression coalesce => Coalesce(coalesce, bindings, parameters),
                GqlNullIfExpression nullIf => NullIf(nullIf, bindings, parameters),
                GqlMemberExpression member => Member(Evaluate(member.Operand, bindings, parameters), member.Member, member),
                GqlSubscriptExpression subscript => Subscript(subscript, bindings, parameters),
                GqlInExpression membership => In(membership, bindings, parameters),
                GqlStringPredicateExpression predicate => StringPredicate(predicate, bindings, parameters),
                _ => throw new GraphPlanningException("COHDBG001", "Unsupported graph expression."),
            };
        }
        catch (ArithmeticException error)
        {
            throw new GraphPlanningException("COHDBG013", ErrorMessages.ArithmeticFailure, expression.Location, error);
        }
    }

    internal static object? Scalar(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters)
    {
        object? value = expression switch
        {
            GqlLiteralExpression literal => literal.Value,
            GqlParameterExpression parameter => Parameter(parameter, parameters),
            _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.PropertyScalarRequired, expression?.Location),
        };
        if (GraphCompositeValues.IsComposite(value))
        { throw new GraphPlanningException("COHDBG003", ErrorMessages.ParameterScalarRequired, expression.Location); }
        return value;
    }

    internal static object? Parameter(GqlParameterExpression parameter, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameter.Name is null || parameters is null || !parameters.TryGetValue(parameter.Name, out var value))
        {
            throw new GraphPlanningException("COHDBG001", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ParameterUnbound, parameter.Name), parameter.Location);
        }
        return value;
    }

    internal static object? ParameterOrLiteral(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters)
        => expression is GqlParameterExpression parameter ? Parameter(parameter, parameters) : ((GqlLiteralExpression)expression).Value;

    // WHERE and every Boolean operator accept only Boolean or NULL; no truthiness coercion.
    internal static bool? Boolean(object? value, GqlExpression? expression = null) => value switch
    {
        null => null,
        bool boolean => boolean,
        _ => throw new GraphPlanningException("COHDBG013", ErrorMessages.BooleanRequired, expression?.Location),
    };

    private static object? Logical(GqlLogicalExpression logical, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
    {
        bool unknown = false;
        bool parity = false;
        foreach (var operand in logical.Operands)
        {
            bool? value = Boolean(Evaluate(operand, bindings, parameters), operand);
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
    // null-extended optional match) is UNKNOWN; AND propagates UNKNOWN unless an
    // operand is false, and WHERE keeps only true.
    private static object? Labeled(GqlLabeledPredicate labeled, IReadOnlyDictionary<string, object?> bindings)
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
    internal static object? Property(object? entity, string name) => entity switch
    {
        null => null,
        GraphNode node => node.Properties.TryGetValue(name, out var value) ? value : null,
        GraphRelationship relationship => relationship.Properties.TryGetValue(name, out var value) ? value : null,
        _ => throw new GraphPlanningException("COHDBG003", "Property access requires a node or relationship."),
    };
    private static object? Unary(GqlUnaryExpression unary, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
    {
        var value = Evaluate(unary.Operand, bindings, parameters);
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

    private static object? In(GqlInExpression membership, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
    {
        var value = Evaluate(membership.Operand, bindings, parameters);
        var evaluated = Evaluate(membership.List, bindings, parameters);
        if (evaluated is null) { return null; }
        if (evaluated is not object?[] candidates)
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.CompositeRequired, membership.List.Location); }
        bool unknown = false;
        foreach (var candidate in candidates)
        {
            bool? equal = GraphCompositeValues.Equal(value, candidate);
            if (equal == true) { return true; }
            unknown |= equal is null;
        }
        return unknown ? null : false;
    }

    private static object? StringPredicate(GqlStringPredicateExpression predicate, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = Evaluate(predicate.Left, bindings, parameters);
        var right = Evaluate(predicate.Right, bindings, parameters);
        if (left is not string text || right is not string search) { return null; }
        return predicate.Operator switch
        {
            GqlStringPredicateOperator.StartsWith => text.StartsWith(search, StringComparison.Ordinal),
            GqlStringPredicateOperator.EndsWith => text.EndsWith(search, StringComparison.Ordinal),
            GqlStringPredicateOperator.Contains => text.Contains(search, StringComparison.Ordinal),
            _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.UnsupportedStringPredicate),
        };
    }

    private static object? Binary(GqlBinaryExpression binary, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = Evaluate(binary.Left, bindings, parameters);
        var right = Evaluate(binary.Right, bindings, parameters);
        if (left is null || right is null) { return null; }
        if (binary.Operator is "+" or "-" or "*" or "/" or "%" or "||")
        {
            if (binary.Operator is "+" or "||" && left is string a && right is string b) { return a + b; }
            if (binary.Operator == "||" || !IsNumber(left) || !IsNumber(right))
            { throw new GraphPlanningException("COHDBG013", ErrorMessages.ArithmeticOperands, binary.Location); }
            return Arithmetic(binary.Operator, left, right);
        }
        if (binary.Operator is "=" or "!=" or "<>")
        {
            bool? equal = GraphCompositeValues.Equal(left, right);
            return binary.Operator == "=" ? equal : !equal;
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
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (left is object?[] list && right is object?[] other)
        {
            for (int i = 0; i < Math.Min(list.Length, other.Length); i++)
            {
                if (list[i] is null || other[i] is null) { return null; }
                int? comparison = Compare(list[i]!, other[i]!);
                if (comparison != 0) { return comparison; }
            }
            return list.Length.CompareTo(other.Length);
        }
        if (left is IReadOnlyDictionary<string, object?> map && right is IReadOnlyDictionary<string, object?> second)
        {
            if (map.Count != second.Count) { return map.Count.CompareTo(second.Count); }
            string[] keys = map.Keys.Order(StringComparer.Ordinal).ToArray();
            string[] otherKeys = second.Keys.Order(StringComparer.Ordinal).ToArray();
            for (int i = 0; i < keys.Length; i++)
            { int result = StringComparer.Ordinal.Compare(keys[i], otherKeys[i]); if (result != 0) { return result; } }
            foreach (string key in keys)
            {
                if (map[key] is null || second[key] is null) { return null; }
                int? result = Compare(map[key]!, second[key]!); if (result != 0) { return result; }
            }
            return 0;
        }
        if (IsNumber(left) && IsNumber(right))
        { return GraphValueComparer.CompareNumbers(left, right); }
        return (left, right) switch
        {
            (string a, string b) => StringComparer.Ordinal.Compare(a, b),
            (bool a, bool b) => a.CompareTo(b),
            _ => GraphValueComparer.Instance.Equals(left, right) ? 0 : null,
        };
    }
    private static bool IsNumber(object value) => GraphValueComparer.IsNumber(value);
}
