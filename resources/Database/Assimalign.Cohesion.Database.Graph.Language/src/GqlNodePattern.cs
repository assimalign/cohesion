using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A node pattern with an optional binding, labels, and literal property constraints.</summary>
/// <param name="Variable">The binding name, or null for an anonymous node.</param>
/// <param name="Labels">
/// The required labels. The parser fills it only when <see cref="LabelExpression"/> is a pure
/// conjunction of names (<c>:A</c>, <c>:A&amp;B</c>, <c>:A:B</c>, <c>IS A</c>) and leaves it empty
/// for any expression that uses <c>|</c>, <c>!</c> or <c>%</c>.
/// </param>
/// <param name="Properties">The literal properties; null, Boolean, integer, floating point, or string.</param>
public sealed record GqlNodePattern(string? Variable, IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, object?> Properties)
{
    /// <summary>
    /// Gets the ISO/IEC 39075 label expression the node must satisfy, or null when the pattern
    /// names no labels. The parser sets it for every <c>:</c> or <c>IS</c> label specification.
    /// </summary>
    /// <remarks>
    /// When it is set it is the node's whole label requirement, and <see cref="Labels"/> must be
    /// empty or name exactly the labels of its conjunction; the planner rejects any other
    /// combination with <c>COHDBG001</c>. A pattern built with <see cref="Labels"/> alone keeps
    /// its meaning: the node carries every listed label.
    /// </remarks>
    public GqlLabelExpression? LabelExpression { get; init; }
}
