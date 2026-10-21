using Xunit;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// Serializes the tests that observe the client event source: the source and its counters are
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseClientEventSourceCollection), DisableParallelization = true)]
public class DatabaseClientEventSourceCollection
{
}
