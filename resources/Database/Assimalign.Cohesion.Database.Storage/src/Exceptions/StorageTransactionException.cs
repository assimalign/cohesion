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
        : this(StorageTransactionErrorCode.Unspecified, message)
    {
    }

    /// <summary>Initializes a transaction violation with a machine-readable reason.</summary>
    /// <param name="code">The reason for the refusal.</param>
    /// <param name="message">A message describing the violation.</param>
    public StorageTransactionException(StorageTransactionErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the reason for the violation, or <see cref="StorageTransactionErrorCode.Unspecified"/> for a general violation.</summary>
    public StorageTransactionErrorCode Code { get; }
}
