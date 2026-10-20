using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Serializes the tests that observe the graph model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(GraphDatabaseEventSourceCollection), DisableParallelization = true)]
public class GraphDatabaseEventSourceCollection
{
}
