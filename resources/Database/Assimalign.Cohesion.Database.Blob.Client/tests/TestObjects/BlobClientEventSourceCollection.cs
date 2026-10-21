using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>
/// Serializes the tests that observe the Blob client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(BlobClientEventSourceCollection), DisableParallelization = true)]
public class BlobClientEventSourceCollection
{
}
