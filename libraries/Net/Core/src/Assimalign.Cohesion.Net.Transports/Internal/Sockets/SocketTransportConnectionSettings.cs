// Ignore Spelling: awaiter

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal sealed class SocketTransportConnectionSettings
{
    public bool IsServer { get; set; }
    public EndPoint EndPoint { get; init; } = default!;
    public Socket Socket { get; set; } = default!;
    public PipeOptions InputOptions { get; init; } = default!;
    public PipeOptions OutputOptions { get; init; } = default!;
    public PipeScheduler ReceiverScheduler { get; init; } = default!;
    public PipeScheduler SenderScheduler { get; init; } = default!;
    public bool WaitForDataBeforeAllocatingBuffer { get; set; }
    public TransportTraceHandler OnTrace { get; set; } = default!;



    public static SocketTransportConnectionSettings[] GetIOQueueSettings(
        int count,
        bool unsafePreferInLineScheduling = false,
        bool waitForDataBeforeAllocatingBuffer = false,
        long? maxReadBufferSize = 0,
        long? maxWriteBufferSize = 0,
        TransportTraceHandler onTrace = default!)
    {
        var options = new SocketTransportConnectionSettings[count];
        var memoryPool = PipelineMemoryPool.Create();
        var applicationScheduler = unsafePreferInLineScheduling ?
            PipeScheduler.Inline :
            PipeScheduler.ThreadPool;
        var transportScheduler = unsafePreferInLineScheduling ?
            PipeScheduler.Inline :
            new SocketPipeScheduler();
        var awaiterScheduler = OperatingSystem.IsWindows() ?
            transportScheduler :
            PipeScheduler.Inline;

        for (var i = 0; i < count; i++)
        {
            options[i] = new SocketTransportConnectionSettings()
            {
                IsServer = true,
                ReceiverScheduler = transportScheduler,
                SenderScheduler = awaiterScheduler,
                InputOptions = new PipeOptions(
                    memoryPool,
                    applicationScheduler,
                    transportScheduler,
                    maxReadBufferSize ?? 0,
                    maxReadBufferSize ?? 0 / 2,
                    useSynchronizationContext: false),
                OutputOptions = new PipeOptions(
                    memoryPool,
                    transportScheduler,
                    applicationScheduler,
                    maxWriteBufferSize ?? 0,
                    maxWriteBufferSize ?? 0 / 2,
                    useSynchronizationContext: false),
                WaitForDataBeforeAllocatingBuffer = waitForDataBeforeAllocatingBuffer,
                OnTrace = onTrace
            };
        }

        return options;
    }
}