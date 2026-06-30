using System;
using System.IO.Pipelines;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// Builds socket-specific pipe option contexts on top of the shared
/// <see cref="PipeOptionsFactory"/>.
/// </summary>
internal static class SocketPipeOptionsFactory
{
    public static TcpConnectionSettings[] CreateSocketConnectionSettings(
        int count,
        bool unsafePreferInLineScheduling = false,
        bool waitForDataBeforeAllocatingBuffer = false,
        long? maxReadBufferSize = 0,
        long? maxWriteBufferSize = 0)
    {
        var settings = new TcpConnectionSettings[count];

        for (int index = 0; index < count; index++)
        {
            settings[index] = new TcpConnectionSettings()
            {
                PipeOptions = CreateSocketPipeOptions(
                    maxReadBufferSize,
                    maxWriteBufferSize,
                    unsafePreferInLineScheduling),
                WaitForDataBeforeAllocatingBuffer = waitForDataBeforeAllocatingBuffer
            };
        }

        return settings;
    }

    public static SocketPipeOptionsContext CreateSocketPipeOptions(
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

        return new SocketPipeOptionsContext(
            PipeOptionsFactory.CreatePipeOptions(
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
