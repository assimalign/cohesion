using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// Describes one column of a SQL result set: its name, ordinal position, and shared
/// type identity as carried by the wire's result header.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="Ordinal">The zero-based ordinal position of the column.</param>
/// <param name="Type">The column's shared type identity.</param>
public sealed record SqlColumn(string Name, int Ordinal, DatabaseType Type);
