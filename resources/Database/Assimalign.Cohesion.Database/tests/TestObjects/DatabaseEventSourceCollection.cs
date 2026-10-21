using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// Serializes the tests that observe the Database event source: the source is process-wide, so the
/// collection runs alone, after every parallel collection.
/// </summary>
[CollectionDefinition(nameof(DatabaseEventSourceCollection), DisableParallelization = true)]
public class DatabaseEventSourceCollection
{
}
