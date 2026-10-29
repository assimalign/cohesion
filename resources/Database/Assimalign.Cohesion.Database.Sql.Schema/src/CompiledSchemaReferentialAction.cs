namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies the action when a referenced parent row is deleted or its key is updated.</summary>
public enum CompiledSchemaReferentialAction : byte
{
    /// <summary>Refuses the parent change while dependent rows exist.</summary>
    Restrict = 0,
    /// <summary>Propagates a parent deletion or key change in the same transaction.</summary>
    Cascade,
    /// <summary>Checks the final statement state for dependent rows.</summary>
    NoAction,
    /// <summary>Assigns NULL to dependent foreign-key columns.</summary>
    SetNull,
    /// <summary>Assigns defaults to dependent foreign-key columns.</summary>
    SetDefault,
}
