using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Runs the lock-wait tests alone: the stress probe keeps eight sessions and a purge thread busy,
/// and both tests enable the transaction kernel's event source, which every database in the
/// process writes to.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlLockWaitCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "SQL lock waits";
}
