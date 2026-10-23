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

    // Private control flow, caught only by ExecuteDeleteAsync before any apply can
    // use this acquisition path. This never becomes a user diagnostic or wire error.
    private sealed class SqlCascadeLockRetry(LockResource resource, LockMode mode) : Exception
    {
        internal LockResource Resource { get; } = resource;
        internal LockMode Mode { get; } = mode;
    }
}
