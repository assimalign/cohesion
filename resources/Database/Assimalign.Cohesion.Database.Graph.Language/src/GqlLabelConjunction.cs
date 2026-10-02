using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A label conjunction <c>&amp;</c>: matches an element every operand matches. One node holds a
/// whole chain, <c>A&amp;B&amp;C</c>, however long it is, and the Cohesion repeated-colon
/// convenience <c>:A:B:C</c> produces the same node as <c>:A&amp;B&amp;C</c>.
/// </summary>
/// <remarks>
/// The parser builds one node per run of <c>&amp;</c> (or of repeated <c>:</c>) at one level of
/// the precedence ladder, as Neo4j's n-ary <c>Conjunctions</c> does, so a chain of any length is
/// one level of the tree. A parenthesized conjunction that opens a chain merges into it:
/// <c>(A&amp;B)&amp;C</c> is the same node as <c>A&amp;B&amp;C</c>. One in a later position stays a
/// nested node, <c>A&amp;(B&amp;C)</c>; the meaning is the same, and merging it would cost a copy of
/// its operands per enclosing group. A hand-built node with a null operand list, a null operand or
/// fewer than two operands is rejected by the planner with <c>COHDBG001</c>.
/// </remarks>
public sealed record GqlLabelConjunction : GqlLabelExpression
{
    private readonly List<GqlLabelExpression>? _operands;

    /// <summary>Initializes a conjunction over a copy of <paramref name="operands"/>.</summary>
    /// <param name="operands">The operands in source order; a valid conjunction has at least two.</param>
    public GqlLabelConjunction(IEnumerable<GqlLabelExpression> operands)
        : this(operands is null ? null : new List<GqlLabelExpression>(operands))
    {
    }

    private GqlLabelConjunction(List<GqlLabelExpression>? operands)
    {
        _operands = operands;
        Operands = operands is null ? null! : new ReadOnlyCollection<GqlLabelExpression>(operands);
    }

    /// <summary>Creates a conjunction that takes ownership of <paramref name="operands"/>, without copying it.</summary>
    /// <param name="operands">The operand list the parser built, which the node now owns.</param>
    /// <returns>The conjunction.</returns>
    internal static GqlLabelConjunction FromOwnedList(List<GqlLabelExpression> operands) => new(operands);

    /// <summary>Gets the operands in source order. A parsed conjunction has at least two.</summary>
    public IReadOnlyList<GqlLabelExpression> Operands { get; }

    /// <summary>
    /// Hands the operand list to the chain that absorbs this node. Only the parser calls this, on a
    /// parenthesized conjunction it just built that opens a longer conjunction, and it drops this
    /// node afterwards, so the chain appends to the list instead of copying it.
    /// </summary>
    /// <returns>The operand list, which the caller now owns.</returns>
    internal List<GqlLabelExpression> DetachOperands() => _operands!;

    /// <summary>Whether <paramref name="other"/> is the same tree: the same operands, in order, compared structurally.</summary>
    /// <param name="other">The conjunction to compare.</param>
    /// <returns><see langword="true"/> when the trees are equal.</returns>
    public bool Equals(GqlLabelConjunction? other) => other is not null && StructurallyEqual(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => StructuralHash(this);
}
