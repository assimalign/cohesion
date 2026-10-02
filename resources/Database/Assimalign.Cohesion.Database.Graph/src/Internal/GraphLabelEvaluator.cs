using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Validates and evaluates ISO/IEC 39075 label expressions with a typed switch: a node against its
/// label set, a relationship against its one type. No reflection, no text matching.
/// </summary>
/// <remarks>
/// The planner validates every expression before execution, so evaluation sees only the five
/// known kinds, no null operand, and at most <see cref="MaximumDepth"/> levels of recursion.
/// </remarks>
internal static class GraphLabelEvaluator
{
    /// <summary>The deepest label expression the engine accepts, as the parser bounds it.</summary>
    internal const int MaximumDepth = 128;

    /// <summary>
    /// Validates an expression's shape and lists the names it mentions, in source order. A null
    /// operand or name, an unknown kind, or nesting past <see cref="MaximumDepth"/> is
    /// <c>COHDBG001</c>.
    /// </summary>
    /// <param name="expression">The expression to validate.</param>
    /// <returns>Every name the expression mentions, including names under <c>!</c> and <c>|</c>.</returns>
    /// <exception cref="DatabaseException">The expression is malformed.</exception>
    internal static IReadOnlyList<string> Names(GqlLabelExpression? expression)
    {
        var names = new List<string>();
        Collect(expression, names, 1);
        return names;

        static void Collect(GqlLabelExpression? expression, List<string> names, int depth)
        {
            if (depth > MaximumDepth)
            {
                throw new DatabaseException($"COHDBG001: Label-expression nesting exceeds {MaximumDepth} levels.");
            }
            switch (expression)
            {
                case GqlLabelName { Name: { } name }:
                    names.Add(name);
                    return;
                case GqlLabelWildcard:
                    return;
                case GqlLabelNegation negation:
                    Collect(negation.Operand, names, depth + 1);
                    return;
                case GqlLabelConjunction conjunction:
                    Collect(conjunction.Left, names, depth + 1);
                    Collect(conjunction.Right, names, depth + 1);
                    return;
                case GqlLabelDisjunction disjunction:
                    Collect(disjunction.Left, names, depth + 1);
                    Collect(disjunction.Right, names, depth + 1);
                    return;
                case null:
                    throw new DatabaseException("COHDBG001: A label expression has a missing operand.");
                case GqlLabelName:
                    throw new DatabaseException("COHDBG001: A label name cannot be null.");
                default:
                    throw new DatabaseException("COHDBG001: Unsupported label expression; use a name, %, !, & or |.");
            }
        }
    }

    /// <summary>
    /// The names of a pure conjunction (<c>A</c>, <c>A&amp;B</c>), or <see langword="null"/> when
    /// the expression uses <c>|</c>, <c>!</c> or <c>%</c>. Call after <see cref="Names"/>.
    /// </summary>
    /// <param name="expression">A validated expression.</param>
    /// <returns>The conjunction's names, or <see langword="null"/>.</returns>
    internal static IReadOnlyList<string>? Conjunction(GqlLabelExpression expression)
    {
        var names = new List<string>();
        return Collect(expression, names) ? names : null;

        static bool Collect(GqlLabelExpression expression, List<string> names)
        {
            switch (expression)
            {
                case GqlLabelName name:
                    names.Add(name.Name);
                    return true;
                case GqlLabelConjunction conjunction:
                    return Collect(conjunction.Left, names) && Collect(conjunction.Right, names);
                default:
                    return false;
            }
        }
    }

    /// <summary>Whether a node with <paramref name="labels"/> satisfies a validated expression.</summary>
    /// <param name="expression">The validated expression.</param>
    /// <param name="labels">The node's labels.</param>
    /// <returns><see langword="true"/> when the node matches.</returns>
    internal static bool Matches(GqlLabelExpression expression, IReadOnlyList<string> labels) => expression switch
    {
        GqlLabelName name => Contains(labels, name.Name),
        GqlLabelWildcard => labels.Count != 0,
        GqlLabelNegation negation => !Matches(negation.Operand, labels),
        GqlLabelConjunction conjunction => Matches(conjunction.Left, labels) && Matches(conjunction.Right, labels),
        GqlLabelDisjunction disjunction => Matches(disjunction.Left, labels) || Matches(disjunction.Right, labels),
        _ => throw new DatabaseException("COHDBG001: Unsupported label expression."),
    };

    /// <summary>
    /// Whether a relationship of <paramref name="type"/> satisfies a validated expression. Every
    /// stored relationship has a type, so <c>%</c> matches it.
    /// </summary>
    /// <param name="expression">The validated expression.</param>
    /// <param name="type">The relationship's type.</param>
    /// <returns><see langword="true"/> when the relationship matches.</returns>
    internal static bool Matches(GqlLabelExpression expression, string type) => expression switch
    {
        GqlLabelName name => string.Equals(name.Name, type, StringComparison.Ordinal),
        GqlLabelWildcard => true,
        GqlLabelNegation negation => !Matches(negation.Operand, type),
        GqlLabelConjunction conjunction => Matches(conjunction.Left, type) && Matches(conjunction.Right, type),
        GqlLabelDisjunction disjunction => Matches(disjunction.Left, type) || Matches(disjunction.Right, type),
        _ => throw new DatabaseException("COHDBG001: Unsupported label expression."),
    };

    /// <summary>Whether a node pattern accepts a node's labels: its expression, or every listed label.</summary>
    /// <param name="pattern">The validated node pattern.</param>
    /// <param name="labels">The node's labels.</param>
    /// <returns><see langword="true"/> when the node's labels satisfy the pattern.</returns>
    internal static bool Accepts(GqlNodePattern pattern, IReadOnlyList<string> labels)
    {
        if (pattern.LabelExpression is { } expression) { return Matches(expression, labels); }
        foreach (string label in pattern.Labels)
        {
            if (!Contains(labels, label)) { return false; }
        }
        return true;
    }

    /// <summary>Whether a relationship pattern accepts a relationship's type: its expression, or its <c>Type</c>.</summary>
    /// <param name="pattern">The validated relationship pattern.</param>
    /// <param name="type">The relationship's type.</param>
    /// <returns><see langword="true"/> when the type satisfies the pattern.</returns>
    internal static bool Accepts(GqlRelationshipPattern pattern, string type) => pattern.LabelExpression is { } expression
        ? Matches(expression, type)
        : pattern.Type is null || string.Equals(pattern.Type, type, StringComparison.Ordinal);

    /// <summary>The labels an inserted node receives: its validated conjunction, or <c>Labels</c>.</summary>
    /// <param name="pattern">A node pattern the planner validated for insertion.</param>
    /// <returns>The labels to create the node with.</returns>
    internal static IReadOnlyList<string> InsertLabels(GqlNodePattern pattern) =>
        pattern.LabelExpression is { } expression ? Conjunction(expression)! : pattern.Labels;

    /// <summary>The type an inserted relationship receives: its <c>Type</c>, or its expression's single name.</summary>
    /// <param name="pattern">A relationship pattern the planner validated for insertion.</param>
    /// <returns>The type to create the relationship with.</returns>
    internal static string InsertType(GqlRelationshipPattern pattern) =>
        pattern.Type ?? ((GqlLabelName)pattern.LabelExpression!).Name;

    private static bool Contains(IReadOnlyList<string> labels, string label)
    {
        for (int i = 0; i < labels.Count; i++)
        {
            if (string.Equals(labels[i], label, StringComparison.Ordinal)) { return true; }
        }
        return false;
    }
}
