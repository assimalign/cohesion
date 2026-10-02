using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A relationship pattern between consecutive node patterns.</summary>
/// <param name="Variable">The optional relationship binding.</param>
/// <param name="Type">
/// The optional required relationship type. The parser fills it only when
/// <see cref="LabelExpression"/> is a single name (<c>:T</c> or <c>IS T</c>).
/// </param>
/// <param name="Direction">The direction relative to the source pattern's node order.</param>
/// <param name="Properties">The literal property constraints.</param>
public sealed record GqlRelationshipPattern(string? Variable, string? Type,
    GqlPatternDirection Direction, IReadOnlyDictionary<string, object?> Properties)
{
    /// <summary>
    /// Gets the ISO/IEC 39075 label expression the relationship's type must satisfy, such as
    /// <c>T|U</c> or <c>!T</c>, or null when the pattern names no type. An abbreviated edge
    /// (<c>-&gt;</c>, <c>&lt;-</c>, <c>-</c>, <c>&lt;-&gt;</c>) never has one.
    /// </summary>
    /// <remarks>
    /// When it is set it is the relationship's whole type requirement, and <see cref="Type"/>
    /// must be null or equal the expression's single name; the planner rejects any other
    /// combination with <c>COHDBG001</c>. A pattern built with <see cref="Type"/> alone keeps its
    /// meaning: the relationship has exactly that type.
    /// </remarks>
    public GqlLabelExpression? LabelExpression { get; init; }
}
