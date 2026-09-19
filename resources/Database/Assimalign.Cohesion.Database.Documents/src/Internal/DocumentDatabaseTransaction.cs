using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context) : IDatabaseTransaction
{
    internal ITransactionContext Context => context;
    internal int Operations { get; set; }
    public TransactionId Id => context.Id;
    public TransactionState State => context.State;
    public IsolationLevel IsolationLevel => context.IsolationLevel;
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (Operations != 0)
        {
            throw new DatabaseException("Dispose every document operation before committing its transaction.");
        }

        return coordinator.CommitAsync(context, cancellationToken);
    }
    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    { EnsureActive(); return coordinator.RollbackAsync(context, cancellationToken); }
    public ValueTask DisposeAsync() => State == TransactionState.Active ? coordinator.RollbackAsync(context) : default;
    private void EnsureActive()
    { if (State != TransactionState.Active)
        {
            throw new DatabaseException($"The transaction is {State}.");
        }
    }
}
