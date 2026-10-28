using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Evaluates scalar expressions with three-valued logic and stack-checked descent.</summary>
internal static partial class GraphExpressionEvaluator
{
    internal static object? Evaluate(GqlExpression expression, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (bindings is GraphIteratorBinding iterator && iterator.TryGetComputed(expression, out var scoped)) { return scoped; }
        if (bindings is GraphGroupedBinding grouped)
        {
            // ORDER BY aliases shadow source variable names, including grouped entities.
            if (expression is GqlVariableExpression name && grouped.TryGetValue(name.Variable, out var aliased)) { return aliased; }
            // ORDER BY's plan excludes reuse through redefined names;
            // RETURN keeps the complete original grouped-expression cache.
            if (grouped.TryGetComputed(expression, out var computed)) { return computed; }
        }
        try
        {
            var result = expression switch
            {
                GqlFunctionCallExpression call => Function(call, bindings, context, parameters),
                GqlNamedFunctionCallExpression call => NamedFunction(call, bindings, context, parameters),
                GqlCastExpression cast => Cast(Evaluate(cast.Operand, bindings, context, parameters), cast),
                GqlLiteralExpression literal => literal.Value,
                GqlParameterExpression parameter => Parameter(parameter, parameters),
                GqlVariableExpression variable => bindings[variable.Variable],
                GqlPropertyExpression property => Member(context.Current(bindings[property.Variable]), property.Property, property),
                GqlBinaryExpression binary => Binary(binary, bindings, context, parameters),
                GqlLogicalExpression logical => Logical(logical, bindings, context, parameters),
                GqlLabeledPredicate labeled => Labeled(labeled, bindings, context),
                GqlUnaryExpression unary => Unary(unary, bindings, context, parameters),
                GqlIsNullExpression test => (Evaluate(test.Operand, bindings, context, parameters) is null) != test.IsNegated,
                GqlTruthTestExpression test => (Boolean(Evaluate(test.Operand, bindings, context, parameters), test.Operand) == test.TruthValue) != test.IsNegated,
                GqlListExpression list => list.Items.Select(item => Evaluate(item, bindings, context, parameters)).ToArray(),
                GqlListComprehensionExpression comprehension => Comprehension(comprehension, bindings, context, parameters),
                GqlReduceExpression reduce => Reduce(reduce, bindings, context, parameters),
                GqlListPredicateExpression predicate => ListPredicate(predicate, bindings, context, parameters),
                GqlListSliceExpression slice => Slice(slice, bindings, context, parameters),
                GqlMapExpression map => Map(map, bindings, context, parameters),
                GqlCaseExpression conditional => Case(conditional, bindings, context, parameters),
                GqlCoalesceExpression coalesce => Coalesce(coalesce, bindings, context, parameters),
                GqlNullIfExpression nullIf => NullIf(nullIf, bindings, context, parameters),
                GqlMemberExpression member => Member(Evaluate(member.Operand, bindings, context, parameters), member.Member, member),
                GqlSubscriptExpression subscript => Subscript(subscript, bindings, context, parameters),
                GqlInExpression membership => In(membership, bindings, context, parameters),
                GqlStringPredicateExpression predicate => StringPredicate(predicate, bindings, context, parameters),
                _ => throw new GraphPlanningException("CG05001", ErrorMessages.EvaluatorEvaluateGraphPlanning),
            };
            result = context.Current(result);
            if (result is string || GraphCompositeValues.IsComposite(result))
            {
                try { GraphCompositeEncoding.Measure(result); }
                catch (DatabaseTypeException error)
                { throw new GraphPlanningException("CG02013", error.Message, expression.Location, error); }
            }
            return result;
        }
        catch (ArithmeticException error)
        {
            throw new GraphPlanningException("CG02013", ErrorMessages.ArithmeticFailure, expression.Location, error);
        }
    }

    internal static object? PropertyScalar(GqlExpression expression, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var value = Evaluate(expression, bindings, context, parameters);
        if (GraphCompositeValues.IsComposite(value) || value is GraphNode or GraphRelationship or GraphPath)
        { throw new GraphPlanningException("CG03003", ErrorMessages.PropertyScalarRequired, expression.Location); }
        try { GraphDatabase.TypeOf(value); }
        catch (DatabaseException error)
        { throw new GraphPlanningException("CG02013", ErrorMessages.PropertyScalarRequired, expression.Location, error); }
        return value;
    }

    internal static object? Scalar(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters)
    {
        object? value = expression switch
        {
            GqlLiteralExpression literal => literal.Value,
            GqlParameterExpression parameter => Parameter(parameter, parameters),
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.PropertyScalarRequired, expression?.Location),
        };
        if (GraphCompositeValues.IsComposite(value))
        { throw new GraphPlanningException("CG03003", ErrorMessages.ParameterScalarRequired, expression.Location); }
        return value;
    }

    internal static object? Parameter(GqlParameterExpression parameter, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameter.Name is null || parameters is null || !parameters.TryGetValue(parameter.Name, out var value))
        {
            throw new GraphPlanningException("CG05001", string.Format(CultureInfo.CurrentCulture,
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
        _ => throw new GraphPlanningException("CG02013", ErrorMessages.BooleanRequired, expression?.Location),
    };

    private static object? Logical(GqlLogicalExpression logical, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        bool unknown = false;
        bool parity = false;
        foreach (var operand in logical.Operands)
        {
            bool? value = Boolean(Evaluate(operand, bindings, context, parameters), operand);
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
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.EvaluatorLogicalGraphPlanning),
        };
    }

    // ISO <labeled predicate>: true or false for a bound element. A variable with no binding (a
    // null-extended optional match) is UNKNOWN; AND propagates UNKNOWN unless an
    // operand is false, and WHERE keeps only true.
    private static object? Labeled(GqlLabeledPredicate labeled, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context)
    {
        if (!bindings.TryGetValue(labeled.Variable, out var entity) || entity is null) { return null; }
        bool matches = context.Current(entity) switch
        {
            GraphNode node => GraphLabelEvaluator.Matches(labeled.LabelExpression, node.Labels),
            GraphRelationship relationship => GraphLabelEvaluator.Matches(labeled.LabelExpression, relationship.Type),
            _ => throw new GraphPlanningException("CG03003", ErrorMessages.EvaluatorLabeledGraphPlanning),
        };
        return matches != labeled.IsNegated;
    }
    internal static object? Property(object? entity, string name) => entity switch
    {
        null => null,
        GraphNode node => node.Properties.TryGetValue(name, out var value) ? value : null,
        GraphRelationship relationship => relationship.Properties.TryGetValue(name, out var value) ? value : null,
        IReadOnlyDictionary<string, object?> map => map.TryGetValue(name, out var value) ? value : null,
        _ => throw new GraphPlanningException("CG03003", ErrorMessages.EvaluatorPropertyGraphPlanning),
    };
    private static object? Unary(GqlUnaryExpression unary, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var value = Evaluate(unary.Operand, bindings, context, parameters);
        if (unary.Operator == "NOT") { return !Boolean(value, unary.Operand); }
        if (value is null) { return null; }
        if (!IsNumber(value)) { throw new GraphPlanningException("CG02013", ErrorMessages.NumericRequired, unary.Location); }
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

    private static object? In(GqlInExpression membership, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var value = Evaluate(membership.Operand, bindings, context, parameters);
        var evaluated = Evaluate(membership.List, bindings, context, parameters);
        if (evaluated is null) { return null; }
        if (evaluated is not object?[] candidates)
        { throw new GraphPlanningException("CG02013", ErrorMessages.CompositeRequired, membership.List.Location); }
        bool unknown = false;
        foreach (var candidate in candidates)
        {
            bool? equal = GraphCompositeValues.Equal(value, candidate);
            if (equal == true) { return true; }
            unknown |= equal is null;
        }
        return unknown ? null : false;
    }

    private static object? StringPredicate(GqlStringPredicateExpression predicate, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = Evaluate(predicate.Left, bindings, context, parameters);
        var right = Evaluate(predicate.Right, bindings, context, parameters);
        if (left is not string text || right is not string search) { return null; }
        return predicate.Operator switch
        {
            GqlStringPredicateOperator.StartsWith => text.StartsWith(search, StringComparison.Ordinal),
            GqlStringPredicateOperator.EndsWith => text.EndsWith(search, StringComparison.Ordinal),
            GqlStringPredicateOperator.Contains => text.Contains(search, StringComparison.Ordinal),
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.UnsupportedStringPredicate),
        };
    }

    private static object? Binary(GqlBinaryExpression binary, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        var left = Evaluate(binary.Left, bindings, context, parameters);
        var right = Evaluate(binary.Right, bindings, context, parameters);
        if (left is null || right is null) { return null; }
        if (binary.Operator is "+" or "-" or "*" or "/" or "%" or "||")
        {
            if (binary.Operator is "+" or "||" && left is string a && right is string b) { return a + b; }
            if (binary.Operator == "||" || !IsNumber(left) || !IsNumber(right))
            { throw new GraphPlanningException("CG02013", ErrorMessages.ArithmeticOperands, binary.Location); }
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
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.EvaluatorBinaryGraphPlanning),
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
            (DateOnly a, DateOnly b) => a.CompareTo(b),
            (DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b),
            _ => GraphValueComparer.Instance.Equals(left, right) ? 0 : null,
        };
    }
    private static bool IsNumber(object value) => GraphValueComparer.IsNumber(value);
}
