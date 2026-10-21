using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Serializes the tests that observe the SQL model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(SqlDatabaseEventSourceCollection), DisableParallelization = true)]
public class SqlDatabaseEventSourceCollection
{
}
