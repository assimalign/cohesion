using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>Describes a column in a materialized SQL protocol result.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The column's shared type identity.</param>
/// <param name="MaxLength">The declared character or binary length; null if unbounded or computed.</param>
public sealed record DatabaseClientColumn(string Name, DatabaseType Type, int? MaxLength = null);

