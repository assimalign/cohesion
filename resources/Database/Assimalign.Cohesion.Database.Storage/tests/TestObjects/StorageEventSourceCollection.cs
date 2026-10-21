using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Serializes the tests that observe the storage event source: the source and its counters are
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(StorageEventSourceCollection), DisableParallelization = true)]
public class StorageEventSourceCollection
{
}
