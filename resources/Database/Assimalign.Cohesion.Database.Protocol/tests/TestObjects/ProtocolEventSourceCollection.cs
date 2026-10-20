using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

/// <summary>
/// Serializes the tests that observe the protocol event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(ProtocolEventSourceCollection), DisableParallelization = true)]
public class ProtocolEventSourceCollection
{
}
