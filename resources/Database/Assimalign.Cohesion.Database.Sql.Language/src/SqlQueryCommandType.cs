namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents the top-level SQL command category parsed from a statement.
/// </summary>
public enum SqlQueryCommandType
{
    Unknown = 0,
    Select,
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
}
