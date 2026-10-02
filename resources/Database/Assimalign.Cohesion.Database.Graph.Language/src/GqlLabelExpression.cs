using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// An ISO/IEC 39075 label expression (16.8): a label name, the wildcard <c>%</c>, a negation
/// <c>!</c>, a conjunction <c>&amp;</c> or a disjunction <c>|</c>. The parser binds <c>!</c>
/// tighter than <c>&amp;</c> and <c>&amp;</c> tighter than <c>|</c>. A chain of one operator, such
/// as <c>A|B|C</c> or <c>:A:B:C</c>, is one n-ary node over all of its operands, however many there
/// are, and parentheses group without adding a node.
/// </summary>
/// <remarks>
/// <para>
/// A node pattern's expression is evaluated against the node's label set; a relationship
/// pattern's against the relationship's one type. The engine evaluates only
/// <see cref="GqlLabelName"/>, <see cref="GqlLabelWildcard"/>, <see cref="GqlLabelNegation"/>,
/// <see cref="GqlLabelConjunction"/> and <see cref="GqlLabelDisjunction"/>; it rejects any other
/// derived record, a null operand or name, and a conjunction or disjunction with fewer than two
/// operands.
/// </para>
/// <para>
/// No count or depth limit applies (#1139 follow-up, as Neo4j: its n-ary
/// <c>Conjunctions</c>/<c>Disjunctions</c> carry any number of children and only the stack bounds
/// nesting). Genuine nesting, through parentheses and <c>!</c>, is bounded by the stack of the
/// thread that parses or runs the statement: the parser reports <c>GQL0009</c> and the engine
/// <c>COHDBG008</c> instead of overflowing it. Rendering, equality and hashing walk the tree with
/// an explicit stack, so they hold for a tree of any depth.
/// </para>
/// </remarks>
public abstract record GqlLabelExpression
{
    /// <summary>Initializes a label expression; the five derived records are the only kinds.</summary>
    private protected GqlLabelExpression() { }

    /// <summary>Renders the expression in GQL syntax, parenthesized only where precedence requires.</summary>
    /// <returns>The expression text, such as <c>(A|B)&amp;!C</c>.</returns>
    /// <remarks>
    /// A parsed expression renders to text that parses back to the same tree. A nested chain of
    /// the operator that encloses it renders in parentheses, so a hand-built
    /// <c>A&amp;(B&amp;C)</c> keeps its shape; an invalid hand-built node (a null operand or name,
    /// or an empty chain) renders as <c>&lt;invalid&gt;</c> rather than throwing.
    /// </remarks>
    public sealed override string ToString()
    {
        var builder = new StringBuilder();
        var pending = new Stack<RenderStep>();
        pending.Push(new RenderStep(this, null, false));
        while (pending.TryPop(out var step))
        {
            if (step.Text is { } text)
            {
                builder.Append(text);
                continue;
            }
            if (step.Parenthesize)
            {
                builder.Append('(');
                pending.Push(new RenderStep(null, ")", false));
                pending.Push(new RenderStep(step.Expression, null, false));
                continue;
            }
            switch (step.Expression)
            {
                case GqlLabelName name:
                    WriteName(builder, name.Name);
                    break;
                case GqlLabelWildcard:
                    builder.Append('%');
                    break;
                case GqlLabelNegation negation:
                    // ISO negates one primary, so a negated operator or negation is parenthesized.
                    builder.Append('!');
                    pending.Push(new RenderStep(negation.Operand, null, negation.Operand is not (GqlLabelName or GqlLabelWildcard)));
                    break;
                case GqlLabelConjunction { Operands: { Count: > 0 } operands }:
                    // Conjunction binds tighter than disjunction; a nested conjunction keeps its group.
                    PushChain(pending, operands, "&", static operand => operand is GqlLabelDisjunction or GqlLabelConjunction);
                    break;
                case GqlLabelDisjunction { Operands: { Count: > 0 } operands }:
                    PushChain(pending, operands, "|", static operand => operand is GqlLabelDisjunction);
                    break;
                default:
                    builder.Append("<invalid>");
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Whether two trees are the same: the same kinds in the same shape, names equal ordinally and
    /// chains with the same operands in the same order. Walks both trees with an explicit stack.
    /// </summary>
    /// <param name="left">The first tree.</param>
    /// <param name="right">The second tree.</param>
    /// <returns><see langword="true"/> when the trees are structurally equal.</returns>
    private protected static bool StructurallyEqual(GqlLabelExpression? left, GqlLabelExpression? right)
    {
        var pending = new Stack<(GqlLabelExpression? Left, GqlLabelExpression? Right)>();
        pending.Push((left, right));
        while (pending.TryPop(out var pair))
        {
            var (first, second) = pair;
            if (ReferenceEquals(first, second)) { continue; }
            if (first is null || second is null || first.GetType() != second.GetType()) { return false; }
            switch (first)
            {
                case GqlLabelName name:
                    if (!string.Equals(name.Name, ((GqlLabelName)second).Name, StringComparison.Ordinal)) { return false; }
                    break;
                case GqlLabelWildcard:
                    break;
                case GqlLabelNegation negation:
                    pending.Push((negation.Operand, ((GqlLabelNegation)second).Operand));
                    break;
                case GqlLabelConjunction conjunction:
                    if (!PushOperands(pending, conjunction.Operands, ((GqlLabelConjunction)second).Operands)) { return false; }
                    break;
                case GqlLabelDisjunction disjunction:
                    if (!PushOperands(pending, disjunction.Operands, ((GqlLabelDisjunction)second).Operands)) { return false; }
                    break;
            }
        }
        return true;
    }

    /// <summary>A hash consistent with <see cref="StructurallyEqual"/>, computed with an explicit stack.</summary>
    /// <param name="root">The tree to hash.</param>
    /// <returns>The structural hash code.</returns>
    private protected static int StructuralHash(GqlLabelExpression root)
    {
        var hash = new HashCode();
        var pending = new Stack<GqlLabelExpression?>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            switch (expression)
            {
                case null:
                    hash.Add(0);
                    break;
                case GqlLabelName name:
                    hash.Add(1);
                    hash.Add(name.Name, StringComparer.Ordinal);
                    break;
                case GqlLabelWildcard:
                    hash.Add(2);
                    break;
                case GqlLabelNegation negation:
                    hash.Add(3);
                    pending.Push(negation.Operand);
                    break;
                case GqlLabelConjunction conjunction:
                    hash.Add(4);
                    PushForHash(ref hash, pending, conjunction.Operands);
                    break;
                case GqlLabelDisjunction disjunction:
                    hash.Add(5);
                    PushForHash(ref hash, pending, disjunction.Operands);
                    break;
            }
        }
        return hash.ToHashCode();
    }

    private static void PushChain(Stack<RenderStep> pending, IReadOnlyList<GqlLabelExpression> operands, string separator,
        Func<GqlLabelExpression?, bool> grouped)
    {
        // Pushed last to first, so the first operand renders first.
        for (int i = operands.Count - 1; i >= 0; i--)
        {
            pending.Push(new RenderStep(operands[i], null, grouped(operands[i])));
            if (i > 0) { pending.Push(new RenderStep(null, separator, false)); }
        }
    }

    private static bool PushOperands(Stack<(GqlLabelExpression?, GqlLabelExpression?)> pending,
        IReadOnlyList<GqlLabelExpression>? left, IReadOnlyList<GqlLabelExpression>? right)
    {
        if (ReferenceEquals(left, right)) { return true; }
        if (left is null || right is null || left.Count != right.Count) { return false; }
        for (int i = 0; i < left.Count; i++) { pending.Push((left[i], right[i])); }
        return true;
    }

    private static void PushForHash(ref HashCode hash, Stack<GqlLabelExpression?> pending,
        IReadOnlyList<GqlLabelExpression>? operands)
    {
        if (operands is null)
        {
            hash.Add(-1);
            return;
        }
        hash.Add(operands.Count);
        for (int i = operands.Count - 1; i >= 0; i--) { pending.Push(operands[i]); }
    }

    private static void WriteName(StringBuilder builder, string? name)
    {
        if (name is null)
        {
            builder.Append("<invalid>");
            return;
        }
        bool regular = name.Length != 0 && (char.IsLetter(name[0]) || name[0] == '_');
        for (int i = 1; regular && i < name.Length; i++)
        {
            regular = char.IsLetterOrDigit(name[i]) || name[i] == '_';
        }
        if (regular)
        {
            builder.Append(name);
            return;
        }

        builder.Append('"').Append(name).Append('"');
    }

    /// <summary>One step of the renderer: an expression to write, optionally grouped, or literal text.</summary>
    private readonly record struct RenderStep(GqlLabelExpression? Expression, string? Text, bool Parenthesize);
}
