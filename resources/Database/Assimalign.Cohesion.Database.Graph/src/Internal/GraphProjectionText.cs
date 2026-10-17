using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Names parsed items by normalized source, and source-free ASTs by equivalent syntax.</summary>
internal static class GraphProjectionText
{
    internal static string Describe(GqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression.Text is { } text) { return text; }
        return expression switch
        {
            GqlVariableExpression variable => Name(variable.Variable),
            GqlPropertyExpression property => Name(property.Variable) + "." + Name(property.Property),
            GqlParameterExpression parameter => "$" + parameter.Name,
            GqlLiteralExpression { Value: null } => "NULL",
            GqlLiteralExpression { Value: string value } => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
            GqlLiteralExpression { Value: bool value } => value ? "TRUE" : "FALSE",
            GqlLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture)!,
            GqlBinaryExpression binary => "(" + Describe(binary.Left) + " " + binary.Operator + " " + Describe(binary.Right) + ")",
            GqlUnaryExpression unary => unary.Operator + " (" + Describe(unary.Operand) + ")",
            GqlLogicalExpression logical => "(" + string.Join(" " + logical.Operator.ToString().ToUpperInvariant() + " ", logical.Operands.Select(Describe)) + ")",
            GqlIsNullExpression test => Describe(test.Operand) + (test.IsNegated ? " IS NOT NULL" : " IS NULL"),
            GqlTruthTestExpression test => Describe(test.Operand) + (test.IsNegated ? " IS NOT " : " IS ") +
                (test.TruthValue is null ? "UNKNOWN" : test.TruthValue.Value ? "TRUE" : "FALSE"),
            GqlInExpression membership => Describe(membership.Operand) + " IN [" + string.Join(", ", membership.List.Items.Select(Describe)) + "]",
            GqlStringPredicateExpression predicate => Describe(predicate.Left) + " " + (predicate.Operator switch
                { GqlStringPredicateOperator.StartsWith => "STARTS WITH", GqlStringPredicateOperator.EndsWith => "ENDS WITH", _ => "CONTAINS" }) + " " + Describe(predicate.Right),
            GqlLabeledPredicate labeled => Name(labeled.Variable) + (labeled.IsNegated ? " IS NOT LABELED " : " IS LABELED ") + labeled.LabelExpression,
            _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.ProjectionUnsupported, expression.Location),
        };
    }

    private static string Name(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsLetterOrDigit(character) || character == '_') ? name : "\"" + name + "\"";
}
