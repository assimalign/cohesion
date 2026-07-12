using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Resolves the storage transaction paired with a logical transaction context, so
/// index page mutations ride the same write-ahead scope as the data mutations of
/// the transaction they belong to.
/// </summary>
/// <remarks>
/// The engine owns the pairing: it begins a storage transaction and a manager
/// transaction together and keeps the mapping for the transaction's lifetime.
/// A crash mid-way therefore rolls index and data changes back as one unit.
/// </remarks>
public interface IStorageTransactionSource
{
    /// <summary>
    /// Resolves the storage transaction for the specified transaction context.
    /// </summary>
    /// <param name="context">The logical transaction context.</param>
    /// <returns>The paired storage transaction.</returns>
    IStorageTransaction GetStorageTransaction(ITransactionContext context);
}
