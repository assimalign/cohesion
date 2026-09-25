using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentDatabaseTransaction"/> class.
    /// </summary>
    /// <param name="coordinator">The transaction coordinator that commits and rolls back the transaction.</param>
    /// <param name="context">The transaction context this transaction wraps.</param>
    public DocumentDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context)
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
            throw new DatabaseException("Dispose every document operation before committing its transaction.");
        }

        return _coordinator.CommitAsync(_context, cancellationToken);
    }
    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    { EnsureActive(); return _coordinator.RollbackAsync(_context, cancellationToken); }
    public ValueTask DisposeAsync() => State == TransactionState.Active ? _coordinator.RollbackAsync(_context) : default;
    private void EnsureActive()
    { if (State != TransactionState.Active)
        {
            throw new DatabaseException($"The transaction is {State}.");
        }
    }
}
