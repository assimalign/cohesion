namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Identifies a database-scoped permission granted to a principal.
/// </summary>
public enum SqlPermission : byte
{
    /// <summary>Allows reading schema objects.</summary>
    Read = 0,

    /// <summary>Allows writing schema objects.</summary>
    Write,

    /// <summary>Allows both reading and writing schema objects.</summary>
    ReadWrite,

    /// <summary>Allows all supported operations on schema objects.</summary>
    All,
}
