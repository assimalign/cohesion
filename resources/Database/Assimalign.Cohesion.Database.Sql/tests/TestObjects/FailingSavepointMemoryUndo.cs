using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class FailingSavepointMemoryUndo : RecordVersionIndex
{
    internal bool Fail { get; set; } = true;
    internal bool Restored { get; private set; }

    protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key,
        ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
    {
        Restored = true;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key,
        ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected override void OnStatementRolledBackCore()
    {
        if (Fail) { throw new IOException("Injected memory compensation failure"); }
        Restored = true;
    }
}
