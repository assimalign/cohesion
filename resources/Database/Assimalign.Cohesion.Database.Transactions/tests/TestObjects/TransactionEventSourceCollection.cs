using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Serializes the tests that observe the Transactions event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(TransactionEventSourceCollection), DisableParallelization = true)]
public class TransactionEventSourceCollection
{
}
