namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A named table, aliased derived query or parenthesized join item in FROM or JOIN.</summary>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class SqlRelationReference
{
    private protected SqlRelationReference(string? alias)
    {
        Alias = alias;
    }

    /// <summary>Gets the alias that qualifies this relation's columns, or null for an unaliased table or join item.</summary>
    public string? Alias { get; }
}
