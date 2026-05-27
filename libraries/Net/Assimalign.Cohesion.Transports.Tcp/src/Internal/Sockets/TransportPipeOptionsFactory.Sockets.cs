using System;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports.Internal;

internal static partial class TransportPipeOptionsFactory
{
    public static TcpTransportConnectionSettings[] CreateSocketConnectionSettings(
        int count,
        bool unsafePreferInLineScheduling = false,
        bool waitForDataBeforeAllocatingBuffer = false,
        long? maxReadBufferSize = 0,
        long? maxWriteBufferSize = 0)
    {
        var settings = new TcpTransportConnectionSettings[count];

        for (int index = 0; index < count; index++)
        {
            settings[index] = new TcpTransportConnectionSettings()
            {
                IsServer = true,
                PipeOptions = CreateSocketPipeOptions(
                    maxReadBufferSize,
                    maxWriteBufferSize,
                    unsafePreferInLineScheduling),
                WaitForDataBeforeAllocatingBuffer = waitForDataBeforeAllocatingBuffer
            };
        }

        return settings;
    }

    public static SocketTransportPipeOptionsContext CreateSocketPipeOptions(
        long? maxReadBufferSize,
        long? maxWriteBufferSize,
        bool unsafePreferInLineScheduling)
    {
        PipeScheduler applicationScheduler = unsafePreferInLineScheduling
            ? PipeScheduler.Inline
            : PipeScheduler.ThreadPool;
        PipeScheduler transportScheduler = GetSocketTransportScheduler(unsafePreferInLineScheduling);
        PipeScheduler senderScheduler = unsafePreferInLineScheduling
            ? PipeScheduler.Inline
            : OperatingSystem.IsWindows()
                ? transportScheduler
                : PipeScheduler.Inline;

        return new SocketTransportPipeOptionsContext(
            CreatePipeOptions(
                maxReadBufferSize,
                maxWriteBufferSize,
                applicationScheduler,
                transportScheduler),
            transportScheduler,
            senderScheduler);
    }

    private static PipeScheduler GetSocketTransportScheduler(bool unsafePreferInLineScheduling)
    {
        return unsafePreferInLineScheduling
            ? PipeScheduler.Inline
            : new SocketPipeScheduler();
    }
}
