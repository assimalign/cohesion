using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

internal sealed class BackpressuredWriteStream : MemoryStream
{
    private readonly bool _fail;

    internal BackpressuredWriteStream(bool fail = false)
    {
        _fail = fail;
    }

    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        if (_fail) { throw new IOException("Injected transport write failure."); }
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
