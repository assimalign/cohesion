using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Serializes the tests that observe the Indexing event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(IndexEventSourceCollection), DisableParallelization = true)]
public class IndexEventSourceCollection
{
}
