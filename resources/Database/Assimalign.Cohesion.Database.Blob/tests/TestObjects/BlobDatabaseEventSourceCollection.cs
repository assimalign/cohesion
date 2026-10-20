using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// Serializes the tests that observe the blob model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(BlobDatabaseEventSourceCollection), DisableParallelization = true)]
public class BlobDatabaseEventSourceCollection
{
}
