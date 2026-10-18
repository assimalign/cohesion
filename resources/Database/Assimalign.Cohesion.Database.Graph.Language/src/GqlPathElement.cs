namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An element in the source order of a path pattern.</summary>
// Deviates from the repo interface-first rule per design decision: Database AST variants use closed abstract record bases and sealed leaves (owner, 2026-10-04; database-area.md).
public abstract record GqlPathElement
{
    private protected GqlPathElement() { }
}
