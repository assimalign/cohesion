using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;


/// <summary>
/// Runs the read-committed stress probe alone. It keeps four readers, two writers and a purge
/// thread busy, so beside it a timing-sensitive test elsewhere in the suite (a server handshake or
/// idle timeout) can starve on a small CI runner.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KeyValueReadCommittedStressCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "KeyValuePair read-committed stress";
}
