namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Classifies the data-loss risk of a migration step.</summary>
public enum SqlSchemaMigrationSafety : byte
{
    /// <summary>The operation preserves existing logical data.</summary>
    Safe = 0,
    /// <summary>The operation can remove data or make existing values invalid.</summary>
    Destructive,
}
