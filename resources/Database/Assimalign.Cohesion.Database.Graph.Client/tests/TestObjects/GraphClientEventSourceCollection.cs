using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// Serializes the tests that observe the graph client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(GraphClientEventSourceCollection), DisableParallelization = true)]
public class GraphClientEventSourceCollection
{
}
