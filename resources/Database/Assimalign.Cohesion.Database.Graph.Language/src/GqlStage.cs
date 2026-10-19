using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A source-ordered operation on a graph binding table.</summary>
// Deviates from the repo interface-first rule per design decision: Database AST variants are concrete-first with an abstract stage base and sealed leaves (owner, 2026-10-04; database-area.md).
public abstract record GqlStage
{
    /// <summary>Initializes a stage in this assembly's variant family.</summary>
    /// <param name="location">The source span.</param>
    private protected GqlStage(Location? location = null) => Location = location;

    /// <summary>Gets the source span.</summary>
    public Location? Location { get; init; }
}
