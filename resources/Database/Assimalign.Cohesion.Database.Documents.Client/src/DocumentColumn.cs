using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>Describes a document result column.</summary>
/// <param name="Name">The projection or catalog column name.</param>
/// <param name="Ordinal">The zero-based position.</param>
/// <param name="Type">The shared type identity.</param>
public sealed record DocumentColumn(string Name, int Ordinal, DatabaseType Type);
