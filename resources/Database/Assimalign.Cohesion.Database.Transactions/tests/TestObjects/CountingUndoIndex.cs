using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

internal sealed class CountingUndoIndex : RecordVersionIndex
{
    internal int HashCalls { get; set; }
    internal int Clears { get; private set; }

    internal Action? BeforeHash { get; set; }

    public override int GetHashCode() { HashCalls++; BeforeHash?.Invoke(); return 1; }

    protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key,
        ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken) => default;

    protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key,
        ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
    {
        Clears++;
        return default;
    }
}
