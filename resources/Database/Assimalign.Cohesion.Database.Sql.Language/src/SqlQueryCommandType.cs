namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents the top-level SQL command category parsed from a statement.
/// </summary>
public enum SqlQueryCommandType
{
    Unknown = 0,
    Select,
    /// <summary>Describe a data statement without executing it.</summary>
    Explain,
    Insert,
    Update,
    Delete,
    Create,
    Alter,
    Drop,
    /// <summary>Begin a session transaction.</summary>
    Begin,
    /// <summary>Commit the session transaction.</summary>
    Commit,
    /// <summary>Roll back the session transaction.</summary>
    Rollback,
    /// <summary>Empty a set of tables through deferred storage replacement.</summary>
    Truncate,
    /// <summary>Export a table through a caller-owned stream.</summary>
    CopyTo,
    /// <summary>Associate source rows with a target and select ordered actions.</summary>
    Merge,
    /// <summary>Discard logical-session state outside a transaction.</summary>
    Discard,
    /// <summary>Configure the current transaction before its first data statement.</summary>
    SetTransaction,
    /// <summary>Create a named undo scope.</summary>
    Savepoint,
    /// <summary>Release a named undo scope and descendants.</summary>
    ReleaseSavepoint,
    /// <summary>Undo to a named scope, retaining it.</summary>
    RollbackToSavepoint,
}
