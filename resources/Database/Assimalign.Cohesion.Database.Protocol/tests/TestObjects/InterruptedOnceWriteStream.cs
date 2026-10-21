using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

internal sealed class InterruptedOnceWriteStream : MemoryStream
{
    private readonly CancellationTokenSource? _cancellation;

    internal InterruptedOnceWriteStream(CancellationTokenSource? cancellation)
    {
        _cancellation = cancellation;
    }

    internal int WriteAttempts { get; private set; }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (++WriteAttempts == 1)
        {
            if (_cancellation is not null)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected first-send failure before copying bytes.");
        }
        return base.WriteAsync(buffer, cancellationToken);
    }
}
