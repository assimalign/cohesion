namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies the action when a referenced parent row is deleted.</summary>
public enum CompiledSchemaReferentialAction : byte
{
    /// <summary>Refuses deletion while dependent rows exist.</summary>
    Restrict = 0,
    /// <summary>Deletes dependent rows in the same transaction.</summary>
    Cascade,
}
