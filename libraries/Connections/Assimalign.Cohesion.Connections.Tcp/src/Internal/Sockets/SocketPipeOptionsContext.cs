using System;
using System.IO.Pipelines;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

internal sealed class SocketPipeOptionsContext : IDisposable
{
    private bool _isDisposed;

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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        PipeOptions.Dispose();
    }
}
