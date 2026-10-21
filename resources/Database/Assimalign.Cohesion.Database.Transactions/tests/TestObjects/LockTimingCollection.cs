using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

/// <summary>Isolates lock-table timing from other tests in this process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LockTimingCollection
{
    public const string Name = "Lock table timing";
}
