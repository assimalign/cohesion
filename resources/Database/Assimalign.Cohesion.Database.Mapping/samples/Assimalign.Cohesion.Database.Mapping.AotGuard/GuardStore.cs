using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping.AotGuard;

internal sealed class GuardStore : IMappingStore<GuardTransaction>
{
    internal Dictionary<int, GuardEntityMapper.Snapshot> Rows { get; set; } = [];
    internal bool FailSecondWrite { get; set; }
    internal int Rollbacks { get; set; }

    public ValueTask<GuardTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GuardTransaction(this, new Dictionary<int, GuardEntityMapper.Snapshot>(Rows)));
    }
}

internal sealed class GuardTransaction(GuardStore store, Dictionary<int, GuardEntityMapper.Snapshot> staged) : IMappingTransaction
{
    private int _writes;
    private bool _committed;

    internal void Apply(EntityChange<int, GuardEntityMapper.Snapshot> change)
    {
        if (++_writes == 2 && store.FailSecondWrite)
        {
            throw new InvalidOperationException("Injected atomicity failure.");
        }

        if (change.Kind == EntityChangeKind.Deleted)
        {
            staged.Remove(change.Key);
        }
        else
        {
            staged[change.Key] = change.Current;
        }
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Rows = staged;
        _committed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            store.Rollbacks++;
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class GuardWriter : IEntityChangeWriter<int, GuardEntityMapper.Snapshot, GuardTransaction>
{
    public ValueTask ApplyAsync(GuardTransaction transaction, EntityChange<int, GuardEntityMapper.Snapshot> change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Apply(change);
        return ValueTask.CompletedTask;
    }
}
