using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Runs timing tests alone, so other suites do not compete for the CPU while they
/// measure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "Indexing timing";
}
