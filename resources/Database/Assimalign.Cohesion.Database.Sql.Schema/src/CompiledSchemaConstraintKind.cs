namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies a compiled constraint kind.</summary>
public enum CompiledSchemaConstraintKind : byte
{
    /// <summary>A foreign-key reference.</summary>
    Reference = 0,

    /// <summary>A model-specific check constraint.</summary>
    Check,
}
