using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

internal sealed class SqlCopyWireGate : MemoryStream
{
    private int _writes;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Fail { get; set; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _writes) == 1)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (Fail) { throw new IOException("Injected destination failure."); }
        await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}
