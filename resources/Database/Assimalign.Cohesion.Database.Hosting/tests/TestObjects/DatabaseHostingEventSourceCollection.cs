using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Serializes the tests that observe the Database hosting event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseHostingEventSourceCollection), DisableParallelization = true)]
public class DatabaseHostingEventSourceCollection
{
}
