using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>Describes a column in a materialized SQL protocol result.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The column's shared type identity.</param>
public sealed record DatabaseClientColumn(string Name, DatabaseType Type);

