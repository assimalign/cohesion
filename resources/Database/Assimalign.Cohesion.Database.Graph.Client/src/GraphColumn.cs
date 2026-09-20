using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>Describes a scalar graph result column.</summary>
/// <param name="Name">The projection or catalog column name.</param>
/// <param name="Ordinal">The zero-based position.</param>
/// <param name="Type">The shared scalar type identity.</param>
public sealed record GraphColumn(string Name, int Ordinal, DatabaseType Type);

