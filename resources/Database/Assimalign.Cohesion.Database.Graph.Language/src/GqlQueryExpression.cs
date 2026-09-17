using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A finite graph query or read-only catalog statement in one database.</summary>
/// <param name="matches">The patterns whose variables are bound before mutation or projection.</param>
/// <param name="predicate">The optional scalar filter over matched bindings.</param>
/// <param name="creates">The patterns inserted for each matching binding.</param>
/// <param name="deleteVariables">The bound variables to delete.</param>
/// <param name="detachDelete">Whether deleting nodes also deletes incident relationships.</param>
/// <param name="projections">The result columns in source order.</param>
/// <param name="location">The source span.</param>
public sealed class GqlQueryExpression(IReadOnlyList<GqlPathPattern> matches, GqlExpression? predicate,
    IReadOnlyList<GqlPathPattern> creates, IReadOnlyList<string> deleteVariables, bool detachDelete,
    IReadOnlyList<GqlProjection> projections, Location? location = null) : GqlExpression(location)
{
    /// <summary>Gets the finite patterns to match.</summary>
    public IReadOnlyList<GqlPathPattern> Matches { get; } = matches;
    /// <summary>Gets the optional match predicate.</summary>
    public GqlExpression? Predicate { get; } = predicate;
    /// <summary>Gets the patterns to insert.</summary>
    public IReadOnlyList<GqlPathPattern> Creates { get; } = creates;
    /// <summary>Gets the bound node or relationship variables to delete.</summary>
    public IReadOnlyList<string> DeleteVariables { get; } = deleteVariables;
    /// <summary>Gets whether deletion cascades to incident relationships.</summary>
    public bool DetachDelete { get; } = detachDelete;
    /// <summary>Gets the result projections.</summary>
    public IReadOnlyList<GqlProjection> Projections { get; } = projections;
    /// <summary>Gets the optional read-only catalog subject; graph clauses cannot accompany it.</summary>
    public GqlCatalogSurface? CatalogSurface { get; init; }
}
