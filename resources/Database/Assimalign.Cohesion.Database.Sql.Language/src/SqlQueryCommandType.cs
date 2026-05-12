namespace Assimalign.Cohesion.Database.Language.Sql;

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
}
