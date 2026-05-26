using System;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports.Internal;

internal sealed class SocketTransportPipeOptionsContext : IDisposable
{
    private bool _isDisposed;

    public SocketTransportPipeOptionsContext(
        TransportPipeOptionsContext pipeOptions,
        PipeScheduler receiverScheduler,
        PipeScheduler senderScheduler)
    {
        PipeOptions = pipeOptions;
        ReceiverScheduler = receiverScheduler;
        SenderScheduler = senderScheduler;
    }

    public TransportPipeOptionsContext PipeOptions { get; }
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
