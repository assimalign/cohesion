using System;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Creates <see cref="ITransactionLog"/> instances.
/// </summary>
public static class TransactionLog
{
    /// <summary>
    /// Creates an in-memory transaction log for embedded working state and tests.
    /// Appends are trivially durable within the process lifetime.
    /// </summary>
    /// <returns>The transaction log.</returns>
    public static ITransactionLog CreateInMemory() => new InMemoryTransactionLog();

    /// <summary>
    /// Creates a transaction log bound to a storage write-ahead journal: lifecycle
    /// records ride the same journal as page images, and commits acknowledge only
    /// after the journal is durable up to the commit record.
    /// </summary>
    /// <param name="journal">The storage journal.</param>
    /// <returns>The transaction log.</returns>
    public static ITransactionLog CreateJournalBound(IJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new JournalTransactionLog(journal);
    }
}
