using System.Collections.Generic;
using System.Collections.ObjectModel;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A chain of predicates joined by one logical operator, such as
/// <c>a.k = 1 AND n:A AND b.k &gt; 2</c>: one node over all of its operands, however many there are.
/// </summary>
/// <remarks>
/// <para>
/// The parser builds one node per run of <c>AND</c> at one level of the predicate grammar, as
/// Neo4j's n-ary <c>Ands</c> holds a conjunction, so a chain of any length is one level of the
/// tree and no comparison count applies. A parenthesized chain that opens a chain of the same
/// operator merges into it: <c>(p AND q) AND r</c> is the same node as <c>p AND q AND r</c>. A
/// parenthesized chain in a later position stays a nested node, <c>p AND (q AND r)</c>; the meaning
/// is the same, and merging it would cost a copy of its operands per enclosing group. Nesting
/// through parentheses is bounded only by the stack: the parser reports <c>GQL0009</c> and the
/// engine <c>COHDBG008</c> instead of overflowing it.
/// </para>
/// <para>
/// The engine evaluates the operands first to last and stops at the first false one. A hand-built
/// node with an undefined operator, a null operand list, a null operand or fewer than two operands
/// is rejected by the planner with <c>COHDBG001</c>.
/// </para>
/// </remarks>
public sealed class GqlLogicalExpression : GqlExpression
{
    private readonly List<GqlExpression>? _operands;

    /// <summary>Initializes a new instance of the <see cref="GqlLogicalExpression"/> class over a copy of <paramref name="operands"/>.</summary>
    /// <param name="operator">The operator joining the operands.</param>
    /// <param name="operands">The operands in source order; a valid chain has at least two.</param>
    /// <param name="location">The source span.</param>
    public GqlLogicalExpression(GqlLogicalOperator @operator, IEnumerable<GqlExpression> operands, Location? location = null)
        : this(@operator, operands is null ? null : new List<GqlExpression>(operands), location)
    {
    }

    private GqlLogicalExpression(GqlLogicalOperator @operator, List<GqlExpression>? operands, Location? location)
        : base(location)
    {
        Operator = @operator;
        _operands = operands;
        Operands = operands is null ? null! : new ReadOnlyCollection<GqlExpression>(operands);
    }

    /// <summary>Gets the operator that joins the operands.</summary>
    public GqlLogicalOperator Operator { get; }

    /// <summary>Gets the operands in source order. A parsed chain has at least two.</summary>
    public IReadOnlyList<GqlExpression> Operands { get; }

    /// <summary>Creates a chain that takes ownership of <paramref name="operands"/>, without copying it.</summary>
    /// <param name="operator">The operator joining the operands.</param>
    /// <param name="operands">The operand list the parser built, which the node now owns.</param>
    /// <param name="location">The source span.</param>
    /// <returns>The chain.</returns>
    internal static GqlLogicalExpression FromOwnedList(GqlLogicalOperator @operator, List<GqlExpression> operands, Location? location)
        => new(@operator, operands, location);

    /// <summary>
    /// Hands the operand list to the chain that absorbs this node. Only the parser calls this, on a
    /// parenthesized chain it just built that opens a longer chain of the same operator, and it
    /// drops this node afterwards, so the chain appends to the list instead of copying it.
    /// </summary>
    /// <returns>The operand list, which the caller now owns.</returns>
    internal List<GqlExpression> DetachOperands() => _operands!;
}
