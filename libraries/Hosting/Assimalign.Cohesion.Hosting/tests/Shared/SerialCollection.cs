using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

/// <summary>
/// A collection for timing- and CPU-sensitive tests that must not run in parallel with
/// other test collections (process-wide measurements are otherwise polluted).
/// </summary>
[CollectionDefinition(nameof(SerialCollection), DisableParallelization = true)]
public class SerialCollection
{
}
