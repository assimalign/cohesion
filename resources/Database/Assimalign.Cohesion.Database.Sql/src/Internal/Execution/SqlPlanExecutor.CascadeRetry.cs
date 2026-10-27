using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private static ValueTask AcquirePreparationLockAsync(SqlStatementContext statement, LockResource resource,
        LockMode mode, bool cascade, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var locks = statement.Coordinator.LockManager;
        if (!cascade) { return locks.AcquireAsync(statement.Transaction.Sequence, resource, mode, cancellationToken); }
        if (!locks.TryAcquire(statement.Transaction.Sequence, resource, mode)) { throw new SqlCascadeLockRetry(resource, mode); }
        return ValueTask.CompletedTask;
    }

    // Private preparation control flow, caught by ExecutePlainDeleteAsync,
    // PrepareJoinedUpdateIntentsAsync and ExecuteJoinedDeleteAsync before apply.
    // This never becomes a user diagnostic or wire error.
    private sealed class SqlCascadeLockRetry : Exception
    {
        internal SqlCascadeLockRetry(LockResource resource, LockMode mode)
        {
            Resource = resource;
            Mode = mode;
        }

        internal LockResource Resource { get; }
        internal LockMode Mode { get; }
    }
}
