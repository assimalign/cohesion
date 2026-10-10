namespace Assimalign.Cohesion.Database;

/// <summary>
/// Identifies what created a database object, and therefore what is allowed to change it.
/// This separates code-first provisioning from ad-hoc statements: ad-hoc objects remain
/// fully mutable by ad-hoc statements, while schema-owned objects change through schema apply.
/// </summary>
public enum DatabaseObjectOwner : byte
{
    /// <summary>Created by a statement or command on a live session. Fully mutable the same way.</summary>
    Adhoc = 0,

    /// <summary>Created by applying a compiled schema. Only a schema apply may alter or drop it.</summary>
    Schema = 1,
}
