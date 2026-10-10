using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// Serializes the tests that observe the library's event source: the source and its counters are
/// process-wide, so an upgrade another test runs concurrently would show up in them.
/// </summary>
[CollectionDefinition(nameof(EventSourceCollection), DisableParallelization = true)]
public class EventSourceCollection
{
}
