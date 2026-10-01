using Xunit;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Serializes the tests that observe the container's event source: the source is process-wide, so a
/// provider another test builds concurrently would show up in them.
/// </summary>
[CollectionDefinition(nameof(EventSourceCollection), DisableParallelization = true)]
public class EventSourceCollection
{
}
