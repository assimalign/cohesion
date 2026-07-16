using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Describes one column of a client-side result: the name and shared type
/// identity carried by the wire's result header.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The column's shared type identity.</param>
public sealed record DatabaseClientColumn(string Name, DatabaseType Type);
