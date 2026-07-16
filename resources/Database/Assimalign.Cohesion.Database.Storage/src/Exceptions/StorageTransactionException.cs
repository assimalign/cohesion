using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a storage-level transaction violation, such as committing a completed
/// transaction, a write conflict on a page owned by another active transaction, or
/// checkpointing while transactions are active.
/// </summary>
public sealed class StorageTransactionException : StorageException
{
    /// <summary>
    /// Initializes a new <see cref="StorageTransactionException"/> with the specified message.
    /// </summary>
    /// <param name="message">A message describing the violation.</param>
    public StorageTransactionException(string message)
        : base(message)
    {
    }
}
