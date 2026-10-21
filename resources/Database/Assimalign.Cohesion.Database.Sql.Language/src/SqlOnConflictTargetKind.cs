namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>How ON CONFLICT selects unique indexes.</summary>
public enum SqlOnConflictTargetKind
{
    /// <summary>All unique indexes (DO NOTHING only).</summary>
    None,
    /// <summary>Every unique index with exactly the specified column set.</summary>
    Columns,
    /// <summary>A unique index named by ON CONSTRAINT.</summary>
    Constraint,
}
