using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Serializes the tests that observe the SQL client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(SqlClientEventSourceCollection), DisableParallelization = true)]
public class SqlClientEventSourceCollection
{
}
