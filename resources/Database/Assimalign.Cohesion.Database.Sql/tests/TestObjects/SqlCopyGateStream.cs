using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class SqlCopyGateStream : MemoryStream
{
    private int _entered;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Writes { get; private set; }
    internal bool Fail { get; set; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Writes++;
        if (Interlocked.Exchange(ref _entered, 1) == 0)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (Fail) { throw new IOException("Injected destination failure."); }
        await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}
