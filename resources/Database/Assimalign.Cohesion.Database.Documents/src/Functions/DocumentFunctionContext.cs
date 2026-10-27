using System;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>The database, statement cancellation and transaction clock of a function call.</summary>
/// <remarks>A default context has no database or timestamp and a noncancelable token.</remarks>
public readonly ref struct DocumentFunctionContext
{
    /// <summary>Initializes a call context.</summary>
    /// <param name="database">The database name.</param><param name="cancellationToken">The statement token.</param><param name="transactionTimestamp">The transaction start, or null outside a transaction.</param>
    public DocumentFunctionContext(DatabaseName database, CancellationToken cancellationToken, DateTimeOffset? transactionTimestamp = null)
    {
        Database = database;
        CancellationToken = cancellationToken;
        TransactionTimestamp = transactionTimestamp;
    }
    /// <summary>Gets the database, or an empty name for a direct call.</summary>
    public DatabaseName Database { get; }
    /// <summary>Gets the statement cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>Gets the transaction start shared across statements, or null outside a transaction.</summary>
    public DateTimeOffset? TransactionTimestamp { get; }
}
