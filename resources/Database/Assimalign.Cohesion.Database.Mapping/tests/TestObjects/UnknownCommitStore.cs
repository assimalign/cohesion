using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping.Tests;

internal sealed class UnknownCommitStore(bool publish, bool failDispose) : IMappingStore<UnknownCommitTransaction>
{
    internal int BeginCount { get; private set; }
    internal bool Published { get; set; }
    internal bool Disposed { get; set; }

    public ValueTask<UnknownCommitTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginCount++;
        return ValueTask.FromResult(new UnknownCommitTransaction(this, publish, failDispose));
    }
}

internal sealed class UnknownCommitTransaction(UnknownCommitStore store, bool publish, bool failDispose) : IMappingTransaction
{
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Published = publish;
        throw new MappingCommitOutcomeUnknownException("Lost commit acknowledgement.", new InvalidOperationException("Transport lost."));
    }

    public ValueTask DisposeAsync()
    {
        store.Disposed = true;
        if (failDispose)
        {
            throw new InvalidOperationException("Cleanup failed.");
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class UnknownCommitWriter : IEntityChangeWriter<int, MappingTestSnapshot, UnknownCommitTransaction>
{
    public ValueTask ApplyAsync(UnknownCommitTransaction transaction, EntityChange<int, MappingTestSnapshot> change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
