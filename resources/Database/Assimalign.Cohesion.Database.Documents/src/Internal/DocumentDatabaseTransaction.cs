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
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (Operations != 0)
        {
            throw new DatabaseException("Dispose every document operation before committing its transaction.");
        }

        try
        {
            await _coordinator.CommitAsync(_context, cancellationToken).ConfigureAwait(false);
        }
        catch (TransactionAbortedException exception)
        {
            throw Translate(exception);
        }
    }
    /// <summary>
    /// Rolls the transaction back. The token is observed only before the rollback
    /// starts; a started rollback runs to completion and always ends the
    /// transaction (#1226).
    /// </summary>
    /// <param name="cancellationToken">Cancels the rollback before it starts.</param>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActive();
        await RollbackContextAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (State == TransactionState.Active)
        {
            await RollbackContextAsync().ConfigureAwait(false);
        }
    }
    private void EnsureActive()
    { if (State != TransactionState.Active)
        {
            throw new DatabaseException($"The transaction is {State}.");
        }
    }

    private async ValueTask RollbackContextAsync()
    {
        try
        {
            await _coordinator.RollbackAsync(_context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TransactionAbortedException exception)
        {
            // Raised only before the rollback starts: another commit or rollback of the
            // transaction is already running.
            throw Translate(exception);
        }
    }

    // The transaction kernel is a child root with its own exception root; its aborts cross the
    // model boundary as the area root's exceptions, as the SQL and KeyValuePair engines do.
    private static DatabaseTransactionAbortedException Translate(TransactionAbortedException exception)
        => exception is TransactionDeadlockException
            ? new DatabaseTransactionDeadlockException(exception.Message, exception)
            : new DatabaseTransactionAbortedException(exception.Message, exception);
}
