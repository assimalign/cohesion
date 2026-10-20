namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Identifies the action applied when a referenced parent row is deleted.</summary>
public enum SqlCatalogReferentialAction : byte
{
    /// <summary>Refuses deletion while dependent rows exist.</summary>
    Restrict = 0,
    /// <summary>Deletes dependent rows in the same transaction.</summary>
    Cascade,
    /// <summary>Checks dependent rows at statement end.</summary>
    NoAction,
    /// <summary>Assigns NULL to the referencing columns.</summary>
    SetNull,
    /// <summary>Assigns each referencing column's default.</summary>
    SetDefault,
}
