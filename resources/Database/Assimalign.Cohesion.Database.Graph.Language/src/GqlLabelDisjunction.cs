using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A label disjunction <c>|</c>: matches an element any operand matches. One node holds a whole
/// chain, <c>A|B|C</c>, however long it is.
/// </summary>
/// <remarks>
/// The parser builds one node per run of <c>|</c>, as Neo4j's n-ary <c>Disjunctions</c> does, so a
/// chain of any length is one level of the tree. A parenthesized disjunction that opens a chain
/// merges into it: <c>(A|B)|C</c> is the same node as <c>A|B|C</c>. One in a later position stays a
/// nested node, <c>A|(B|C)</c>; the meaning is the same, and merging it would cost a copy of its
/// operands per enclosing group. A hand-built node with a null operand list, a null operand or
/// fewer than two operands is rejected by the planner with <c>COHDBG001</c>.
/// </remarks>
public sealed record GqlLabelDisjunction : GqlLabelExpression
{
    private readonly List<GqlLabelExpression>? _operands;

    /// <summary>Initializes a disjunction over a copy of <paramref name="operands"/>.</summary>
    /// <param name="operands">The operands in source order; a valid disjunction has at least two.</param>
    public GqlLabelDisjunction(IEnumerable<GqlLabelExpression> operands)
        : this(operands is null ? null : new List<GqlLabelExpression>(operands))
    {
    }

    private GqlLabelDisjunction(List<GqlLabelExpression>? operands)
    {
        _operands = operands;
        Operands = operands is null ? null! : new ReadOnlyCollection<GqlLabelExpression>(operands);
    }

    /// <summary>Creates a disjunction that takes ownership of <paramref name="operands"/>, without copying it.</summary>
    /// <param name="operands">The operand list the parser built, which the node now owns.</param>
    /// <returns>The disjunction.</returns>
    internal static GqlLabelDisjunction FromOwnedList(List<GqlLabelExpression> operands) => new(operands);

    /// <summary>Gets the operands in source order. A parsed disjunction has at least two.</summary>
    public IReadOnlyList<GqlLabelExpression> Operands { get; }

    /// <summary>
    /// Hands the operand list to the chain that absorbs this node. Only the parser calls this, on a
    /// parenthesized disjunction it just built that opens a longer disjunction, and it drops this
    /// node afterwards, so the chain appends to the list instead of copying it.
    /// </summary>
    /// <returns>The operand list, which the caller now owns.</returns>
    internal List<GqlLabelExpression> DetachOperands() => _operands!;

    /// <summary>Whether <paramref name="other"/> is the same tree: the same operands, in order, compared structurally.</summary>
    /// <param name="other">The disjunction to compare.</param>
    /// <returns><see langword="true"/> when the trees are equal.</returns>
    public bool Equals(GqlLabelDisjunction? other) => other is not null && StructurallyEqual(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => StructuralHash(this);
}
