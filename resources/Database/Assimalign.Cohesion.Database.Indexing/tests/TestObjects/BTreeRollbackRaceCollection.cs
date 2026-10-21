using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Runs the rollback race tests alone: they keep a writer and three cursors busy and time a
/// latch hold, so other tests running beside them would starve them on a small CI runner.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BTreeRollbackRaceCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "B-tree rollback races";
}
