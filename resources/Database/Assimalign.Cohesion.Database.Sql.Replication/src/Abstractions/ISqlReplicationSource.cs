using System;

namespace Assimalign.Cohesion.Database.Sql.Replication;

/// <summary>
/// Marker seam for SQL-model replication semantics on the shared replication
/// contracts. Relational replication (schema-change ordering, row-level change
/// streams) is defined here as the shared `Database.Replication` log-shipping
/// contracts are built out.
/// </summary>
public interface ISqlReplicationSource
{
}
