using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Validates and evaluates ISO/IEC 39075 label expressions with a typed switch: a node against its
/// label set, a relationship against its one type. No reflection, no text matching.
/// </summary>
/// <remarks>
/// <para>
/// A conjunction or disjunction is one n-ary node, so a chain of any length is evaluated by a loop,
/// and no count or depth limit applies (as Neo4j). Validation and the name walks use an explicit
/// stack and hold for a tree of any shape. Evaluation recurses only where the tree nests (a group
/// inside a chain, or a negation), and checks the stack before each descent: a tree deeper than the
/// executing thread's stack fails its statement with <c>COHDBG007</c> instead of overflowing it.
/// </para>
/// <para>
/// The planner validates every expression before execution, so evaluation sees only the five
/// known kinds, no null operand, and chains of at least two operands.
/// </para>
/// </remarks>
internal static class GraphLabelEvaluator
{
    // A node with more labels than this is tested through a hash set built once per evaluation, so
    // a long chain costs its length plus the label count rather than their product.
    private const int linearLabelLimit = 16;

    // Planner messages quote an expression; a long chain is cut here so the message stays readable.
    private const int describedLength = 256;

    /// <summary>
    /// Validates an expression's shape and lists the names it mentions, in source order. A null
    /// operand, name or operand list, a chain with fewer than two operands, or an unknown kind is
    /// <c>COHDBG001</c>.
    /// </summary>
    /// <param name="expression">The expression to validate.</param>
    /// <returns>Every name the expression mentions, including names under <c>!</c> and <c>|</c>.</returns>
    /// <exception cref="DatabaseException">The expression is malformed.</exception>
    internal static IReadOnlyList<string> Names(GqlLabelExpression? expression)
    {
        var names = new List<string>();
        var pending = new Stack<GqlLabelExpression?>();
        pending.Push(expression);
        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case GqlLabelName { Name: { } name }:
                    names.Add(name);
                    break;
                case GqlLabelWildcard:
                    break;
                case GqlLabelNegation negation:
                    pending.Push(negation.Operand);
                    break;
                case GqlLabelConjunction conjunction:
                    PushOperands(pending, conjunction.Operands, "conjunction");
                    break;
                case GqlLabelDisjunction disjunction:
                    PushOperands(pending, disjunction.Operands, "disjunction");
                    break;
                case null:
                    throw new DatabaseException("COHDBG001: A label expression has a missing operand.");
                case GqlLabelName:
                    throw new DatabaseException("COHDBG001: A label name cannot be null.");
                default:
                    throw new DatabaseException("COHDBG001: Unsupported label expression; use a name, %, !, & or |.");
            }
        }
        return names;

        static void PushOperands(Stack<GqlLabelExpression?> pending, IReadOnlyList<GqlLabelExpression>? operands, string kind)
        {
            if (operands is null) { throw new DatabaseException($"COHDBG001: A label {kind} has no operand list."); }
            if (operands.Count < 2) { throw new DatabaseException($"COHDBG001: A label {kind} needs at least two operands."); }
            // Pushed last to first, so names list in source order.
            for (int i = operands.Count - 1; i >= 0; i--) { pending.Push(operands[i]); }
        }
    }

    /// <summary>
    /// The names of a pure conjunction (<c>A</c>, <c>A&amp;B</c>, <c>A&amp;(B&amp;C)</c>), in source
    /// order, or <see langword="null"/> when the expression uses <c>|</c>, <c>!</c> or <c>%</c>. Call
    /// after <see cref="Names"/>.
    /// </summary>
    /// <param name="expression">A validated expression.</param>
    /// <returns>The conjunction's names, or <see langword="null"/>.</returns>
    internal static IReadOnlyList<string>? Conjunction(GqlLabelExpression expression)
    {
        var names = new List<string>();
        var pending = new Stack<GqlLabelExpression>();
        pending.Push(expression);
        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case GqlLabelName name:
                    names.Add(name.Name);
                    break;
                case GqlLabelConjunction conjunction:
                    for (int i = conjunction.Operands.Count - 1; i >= 0; i--) { pending.Push(conjunction.Operands[i]); }
                    break;
                default:
                    return null;
            }
        }
        return names;
    }

    /// <summary>Whether a node with <paramref name="labels"/> satisfies a validated expression.</summary>
    /// <param name="expression">The validated expression.</param>
    /// <param name="labels">The node's labels.</param>
    /// <returns><see langword="true"/> when the node matches.</returns>
    /// <exception cref="InsufficientExecutionStackException">The expression nests deeper than the thread's stack allows.</exception>
    internal static bool Matches(GqlLabelExpression expression, IReadOnlyList<string> labels)
        => Matches(expression, new LabelSet(labels));

    /// <summary>
    /// Whether a relationship of <paramref name="type"/> satisfies a validated expression. Every
    /// stored relationship has a type, so <c>%</c> matches it.
    /// </summary>
    /// <param name="expression">The validated expression.</param>
    /// <param name="type">The relationship's type.</param>
    /// <returns><see langword="true"/> when the relationship matches.</returns>
    /// <exception cref="InsufficientExecutionStackException">The expression nests deeper than the thread's stack allows.</exception>
    internal static bool Matches(GqlLabelExpression expression, string type)
    {
        switch (expression)
        {
            case GqlLabelName name:
                return string.Equals(name.Name, type, StringComparison.Ordinal);
            case GqlLabelWildcard:
                return true;
            case GqlLabelNegation negation:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                return !Matches(negation.Operand, type);
            case GqlLabelConjunction conjunction:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var operand in conjunction.Operands)
                {
                    if (!Matches(operand, type)) { return false; }
                }
                return true;
            case GqlLabelDisjunction disjunction:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var operand in disjunction.Operands)
                {
                    if (Matches(operand, type)) { return true; }
                }
                return false;
            default:
                throw new DatabaseException("COHDBG001: Unsupported label expression.");
        }
    }

    /// <summary>Whether a node pattern accepts a node's labels: its expression, or every listed label.</summary>
    /// <param name="pattern">The validated node pattern.</param>
    /// <param name="labels">The node's labels.</param>
    /// <returns><see langword="true"/> when the node's labels satisfy the pattern.</returns>
    internal static bool Accepts(GqlNodePattern pattern, IReadOnlyList<string> labels)
    {
        if (pattern.LabelExpression is { } expression) { return Matches(expression, labels); }
        var set = new LabelSet(labels);
        foreach (string label in pattern.Labels)
        {
            if (!set.Contains(label)) { return false; }
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

    /// <summary>
    /// The labels an inserted node receives: its validated conjunction, or <c>Labels</c>, each once,
    /// in first-mention order. <c>:A&amp;A</c> labels the node <c>A</c> once.
    /// </summary>
    /// <param name="pattern">A node pattern the planner validated for insertion.</param>
    /// <returns>The labels to create the node with.</returns>
    internal static IReadOnlyList<string> InsertLabels(GqlNodePattern pattern)
        => Distinct(pattern.LabelExpression is { } expression ? Conjunction(expression)! : pattern.Labels);

    /// <summary>The type an inserted relationship receives: its <c>Type</c>, or its expression's single name.</summary>
    /// <param name="pattern">A relationship pattern the planner validated for insertion.</param>
    /// <returns>The type to create the relationship with.</returns>
    internal static string InsertType(GqlRelationshipPattern pattern) =>
        pattern.Type ?? ((GqlLabelName)pattern.LabelExpression!).Name;

    /// <summary>Each name once, in first-mention order.</summary>
    /// <param name="names">The names, possibly repeated.</param>
    /// <returns>The distinct names.</returns>
    internal static IReadOnlyList<string> Distinct(IReadOnlyList<string> names)
    {
        if (names.Count < 2) { return names; }
        var seen = new HashSet<string>(names.Count, StringComparer.Ordinal);
        var distinct = new List<string>(names.Count);
        foreach (string name in names)
        {
            if (seen.Add(name)) { distinct.Add(name); }
        }
        return distinct.Count == names.Count ? names : distinct;
    }

    /// <summary>
    /// The expression as GQL text for a diagnostic, cut to a readable length: a chain of thousands
    /// of labels would otherwise fill the message.
    /// </summary>
    /// <param name="expression">The expression to describe.</param>
    /// <returns>The text, ending in <c>...</c> when it was cut.</returns>
    internal static string Describe(GqlLabelExpression expression)
    {
        string text = expression.ToString();
        return text.Length <= describedLength ? text : string.Concat(text.AsSpan(0, describedLength), "...");
    }

    private static bool Matches(GqlLabelExpression expression, in LabelSet labels)
    {
        switch (expression)
        {
            case GqlLabelName name:
                return labels.Contains(name.Name);
            case GqlLabelWildcard:
                return labels.Count != 0;
            case GqlLabelNegation negation:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                return !Matches(negation.Operand, labels);
            case GqlLabelConjunction conjunction:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var operand in conjunction.Operands)
                {
                    if (!Matches(operand, labels)) { return false; }
                }
                return true;
            case GqlLabelDisjunction disjunction:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var operand in disjunction.Operands)
                {
                    if (Matches(operand, labels)) { return true; }
                }
                return false;
            default:
                throw new DatabaseException("COHDBG001: Unsupported label expression.");
        }
    }

    /// <summary>A node's labels, looked up linearly when there are few and through a hash set otherwise.</summary>
    private readonly struct LabelSet
    {
        private readonly IReadOnlyList<string> _labels;
        private readonly HashSet<string>? _set;

        internal LabelSet(IReadOnlyList<string> labels)
        {
            _labels = labels;
            _set = labels.Count > linearLabelLimit ? new HashSet<string>(labels, StringComparer.Ordinal) : null;
        }

        internal int Count => _labels.Count;

        internal bool Contains(string label)
        {
            if (_set is not null) { return _set.Contains(label); }
            for (int i = 0; i < _labels.Count; i++)
            {
                if (string.Equals(_labels[i], label, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }
    }
}
