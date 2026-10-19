using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// The collection of tests that race a writer against readers of the same pages. It runs alone:
/// beside other tests on a small CI runner the writer can be starved until the race never
/// happens, or starve the tests beside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StorageRaceCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Storage races";
}
