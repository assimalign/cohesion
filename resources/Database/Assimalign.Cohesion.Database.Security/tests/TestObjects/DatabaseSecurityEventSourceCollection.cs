using Xunit;

namespace Assimalign.Cohesion.Database.Security.Tests;

/// <summary>
/// Serializes the tests that observe the security event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseSecurityEventSourceCollection), DisableParallelization = true)]
public class DatabaseSecurityEventSourceCollection
{
}
