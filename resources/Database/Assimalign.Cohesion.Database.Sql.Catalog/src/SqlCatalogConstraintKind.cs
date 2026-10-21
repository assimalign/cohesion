namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Identifies a persisted relational constraint. Unique constraints use unique indexes.</summary>
public enum SqlCatalogConstraintKind : byte
{
    /// <summary>A foreign key requiring a matching referenced row.</summary>
    Reference = 0,
    /// <summary>A Boolean expression checked for each written row.</summary>
    Check,
}
