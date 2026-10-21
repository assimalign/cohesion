using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// Serializes the tests that observe the key-value client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(KeyValueClientEventSourceCollection), DisableParallelization = true)]
public class KeyValueClientEventSourceCollection
{
}
