using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Retry only an admission boundary, never the prepared migration. Each operation
    // rechecks its protected state after reacquiring its own gates. The delegate must
    // unwind those gates before this catch runs (especially publication's snapshot gate).
    private async ValueTask<T> WithMigrationAdmissionAsync<T>(Func<ValueTask<T>> operation,
        bool catalogStorage, SqlStatementContext statement, CancellationToken token, Action? onRefusal = null,
        bool retryIndexAdmission = false)
    {
        var admission = statement.MigrationAdmission;
        while (true)
        {
            try { return await operation().ConfigureAwait(false); }
            catch (TransactionAdmissionCanceledException) when (!token.IsCancellationRequested &&
                (admission is not null || retryIndexAdmission))
            {
                onRefusal?.Invoke();
                if (admission is not null) { await admission(catalogStorage, token).ConfigureAwait(false); }
                else
                {
                    // The failed bracket unwound every apply/page gate. The logical
                    // writer lock survives while checkpoint admission waits here.
                    using var deadline = !catalogStorage || statement.MetadataLockTimeout == TimeSpan.Zero ? null
                        : CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline?.CancelAfter(statement.MetadataLockTimeout);
                    var waitingToken = deadline?.Token ?? token;
                    try { await Task.Run(() =>
                    {
                        if (catalogStorage) { _catalog.WaitForIndexAdmission(waitingToken); return; }
                        using var lease = TransactionCheckpointInterlock.ForStorage(_storage)
                            .Enter("SqlIndexBuild", cancellationToken: waitingToken);
                    }, CancellationToken.None).ConfigureAwait(false); }
                    catch (OperationCanceledException failure) when (!token.IsCancellationRequested && deadline?.IsCancellationRequested == true)
                    { throw SqlTimeoutException.Admission(failure); }
                }
            }
            catch (TransactionAdmissionCanceledException failure) when (admission is not null)
            { onRefusal?.Invoke(); throw SqlTimeoutException.Statement(failure, null); }
        }
    }

    private ValueTask<T> WithCatalogMigrationAdmissionAsync<T>(Func<T> operation, SqlStatementContext statement,
        CancellationToken token, Action? onRefusal = null)
        => WithMigrationAdmissionAsync(() => new ValueTask<T>(operation()), true, statement, token, onRefusal,
            retryIndexAdmission: true);

    private async ValueTask CheckMigrationAdmissionAsync(SqlStatementContext statement, CancellationToken token)
    {
        if (statement.MigrationAdmission is null) { return; }
        await WithMigrationAdmissionAsync(() =>
        {
            using var admission = TransactionCheckpointInterlock.ForStorage(_storage)
                .Enter("SqlMigration", cancellationToken: token, waitForAdmission: false);
            return new ValueTask<bool>(true);
        }, false, statement, token).ConfigureAwait(false);
    }
}
