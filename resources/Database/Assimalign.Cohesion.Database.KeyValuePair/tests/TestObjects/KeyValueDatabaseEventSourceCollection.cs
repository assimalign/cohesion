using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// Serializes the tests that observe the key-value model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(KeyValueDatabaseEventSourceCollection), DisableParallelization = true)]
public class KeyValueDatabaseEventSourceCollection
{
}
