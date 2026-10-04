using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobDatabaseTransaction"/> class.
    /// </summary>
    /// <param name="coordinator">The transaction coordinator that commits and rolls back the transaction.</param>
    /// <param name="context">The transaction context this transaction wraps.</param>
    public BlobDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context)
    {
        _coordinator = coordinator;
        _context = context;
    }

    internal ITransactionContext Context => _context;
    internal int Operations { get; set; }
    public TransactionId Id => _context.Id;
    public TransactionState State => _context.State;
    public IsolationLevel IsolationLevel => _context.IsolationLevel;
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (Operations != 0)
        {
            throw new DatabaseException("Dispose every blob stream before committing its transaction.");
        }

        return _coordinator.CommitAsync(_context, cancellationToken);
    }
    /// <summary>
    /// Rolls the transaction back. The token is observed only before the rollback
    /// starts; a started rollback runs to completion and always ends the
    /// transaction (#1226).
    /// </summary>
    /// <param name="cancellationToken">Cancels the rollback before it starts.</param>
    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActive();
        return _coordinator.RollbackAsync(_context, CancellationToken.None);
    }
    public ValueTask DisposeAsync() => State == TransactionState.Active ? _coordinator.RollbackAsync(_context) : default;
    private void EnsureActive()
    { if (State != TransactionState.Active)
        {
            throw new DatabaseException($"The transaction is {State}.");
        }
    }
}
