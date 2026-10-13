using System;
using System.IO.Pipelines;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

internal sealed class SocketPipeOptionsContext : IDisposable
{
    private int _references = 1;

    public SocketPipeOptionsContext(
        PipeOptionsContext pipeOptions,
        PipeScheduler receiverScheduler,
        PipeScheduler senderScheduler)
    {
        PipeOptions = pipeOptions;
        ReceiverScheduler = receiverScheduler;
        SenderScheduler = senderScheduler;
    }

    public PipeOptionsContext PipeOptions { get; }
    public PipeOptions InputOptions => PipeOptions.InputOptions;
    public PipeOptions OutputOptions => PipeOptions.OutputOptions;
    public PipeScheduler ReceiverScheduler { get; }
    public PipeScheduler SenderScheduler { get; }
    public int BlockSize => PipeOptions.BlockSize;

    // The listener and each handed-out connection own one reference to the shared pool.
    public SocketPipeOptionsContext Retain()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            PipeOptions.Dispose();
        }
    }
}
