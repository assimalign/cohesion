using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    private int _migrationWaiting;

    private bool TryEnterApply(CancellationToken token)
    {
        if (!_applyGate.Wait(0, token)) { return false; }
        if (Volatile.Read(ref _migrationWaiting) == 0) { return true; }
        _applyGate.Release();
        return false;
    }

    private Task WaitMigrationAdmissionAsync(bool catalogStorage, CancellationToken token)
        => WaitMigrationAdmissionAsync(catalogStorage, token, applyLockTimeout: true);

    private async Task WaitMigrationAdmissionAsync(bool catalogStorage, CancellationToken token, bool applyLockTimeout)
    {
        // Logical table/name/definition reservations survive. Checkpoints need none
        // of them. No snapshot gate, catalog monitor, coordinator apply gate or
        // page/tree latch may reach here. Competing schema admission still refuses
        // through the reservation bit while the semaphore is released.
        Volatile.Write(ref _migrationWaiting, 1);
        _applyGate.Release();
        try
        {
            using var deadline = !applyLockTimeout || _database.Engine.EngineOptions.LockTimeout == TimeSpan.Zero ? null
                : CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline?.CancelAfter(_database.Engine.EngineOptions.LockTimeout);
            var waitingToken = deadline?.Token ?? token;
            await Task.Run(() =>
            {
                using var admission = TransactionCheckpointInterlock.ForStorage(catalogStorage ? _database.CatalogStorage : _database.DataStorage)
                    .Enter("SqlMigration", cancellationToken: waitingToken);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) { throw SqlTimeoutException.Admission(failure); }
        finally
        {
            // A canceled/closed job must restore ownership before its existing
            // discard and lifecycle finally blocks release it exactly once.
            await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _migrationWaiting, 0);
        }
    }

    private async ValueTask WithCatalogMigrationAdmissionAsync(Func<ValueTask> operation, CancellationToken token)
    {
        while (true)
        {
            try { await operation().ConfigureAwait(false); return; }
            catch (TransactionAdmissionCanceledException) when (!token.IsCancellationRequested)
            { await WaitMigrationAdmissionAsync(true, token).ConfigureAwait(false); }
            catch (TransactionAdmissionCanceledException failure) { throw SqlTimeoutException.Statement(failure, null); }
        }
    }
}
