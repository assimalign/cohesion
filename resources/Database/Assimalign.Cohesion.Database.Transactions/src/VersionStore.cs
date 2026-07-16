namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Creates <see cref="IVersionStore"/> instances.
/// </summary>
public static class VersionStore
{
    /// <summary>
    /// Creates an in-memory version store: per-entry version chains resolved
    /// newest-first against snapshots. Model engines bring page-backed stores;
    /// this one serves embedded working state and tests.
    /// </summary>
    /// <returns>The version store.</returns>
    public static IVersionStore CreateInMemory() => new InMemoryVersionStore();
}
