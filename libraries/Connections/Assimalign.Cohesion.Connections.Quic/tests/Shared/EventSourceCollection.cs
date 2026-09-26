using Xunit;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

/// <summary>
/// Serializes the tests that observe the driver's event source: the source and its counters are
/// process-wide, so a connection another test opens concurrently would show up in them.
/// </summary>
[CollectionDefinition(nameof(EventSourceCollection), DisableParallelization = true)]
public class EventSourceCollection
{
}
