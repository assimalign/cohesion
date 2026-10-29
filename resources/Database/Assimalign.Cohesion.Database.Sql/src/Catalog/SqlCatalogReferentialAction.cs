namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Identifies the configured action when a referenced parent row is deleted or its key is updated.</summary>
public enum SqlCatalogReferentialAction : byte
{
    /// <summary>Refuses a parent change while dependent rows exist.</summary>
    Restrict = 0,
    /// <summary>Propagates a parent deletion or key change in the same transaction.</summary>
    Cascade,
    /// <summary>Checks dependent rows at statement end.</summary>
    NoAction,
    /// <summary>Assigns NULL to the referencing columns.</summary>
    SetNull,
    /// <summary>Assigns each referencing column's default.</summary>
    SetDefault,
}
