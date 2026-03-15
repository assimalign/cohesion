using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports.Internal;

internal static class TransportPipeOptionsFactory
{
    private const int DefaultReadBufferSize = 64 * 1024;
    private const int DefaultWriteBufferSize = 16 * 1024;
    private const int MinimumSegmentSize = 4096;

    public static PipeOptions CreateInputOptions(long? maxReadBufferSize, bool unsafePreferInLineScheduling)
    {
        PipeScheduler applicationScheduler = GetApplicationScheduler(unsafePreferInLineScheduling);
        PipeScheduler transportScheduler = GetTransportScheduler(unsafePreferInLineScheduling);

        return new PipeOptions(
            MemoryPool<byte>.Shared,
            applicationScheduler,
            transportScheduler,
            GetPauseThreshold(maxReadBufferSize),
            GetResumeThreshold(maxReadBufferSize),
            MinimumSegmentSize,
            useSynchronizationContext: false);
    }

    public static PipeOptions CreateOutputOptions(long? maxWriteBufferSize, bool unsafePreferInLineScheduling)
    {
        PipeScheduler applicationScheduler = GetApplicationScheduler(unsafePreferInLineScheduling);
        PipeScheduler transportScheduler = GetTransportScheduler(unsafePreferInLineScheduling);

        return new PipeOptions(
            MemoryPool<byte>.Shared,
            transportScheduler,
            applicationScheduler,
            GetPauseThreshold(maxWriteBufferSize),
            GetResumeThreshold(maxWriteBufferSize),
            MinimumSegmentSize,
            useSynchronizationContext: false);
    }

    public static StreamPipeReaderOptions CreateReaderOptions(long? readBufferSize)
    {
        int bufferSize = ClampBufferSize(readBufferSize, DefaultReadBufferSize);

        return new StreamPipeReaderOptions(
            MemoryPool<byte>.Shared,
            bufferSize,
            Math.Min(bufferSize, MinimumSegmentSize),
            leaveOpen: false);
    }

    public static StreamPipeWriterOptions CreateWriterOptions(long? writeBufferSize)
    {
        return new StreamPipeWriterOptions(
            MemoryPool<byte>.Shared,
            ClampBufferSize(writeBufferSize, DefaultWriteBufferSize),
            leaveOpen: false);
    }

    private static PipeScheduler GetApplicationScheduler(bool unsafePreferInLineScheduling)
    {
        return unsafePreferInLineScheduling
            ? PipeScheduler.Inline
            : PipeScheduler.ThreadPool;
    }

    private static PipeScheduler GetTransportScheduler(bool unsafePreferInLineScheduling)
    {
        return unsafePreferInLineScheduling
            ? PipeScheduler.Inline
            : PipeScheduler.ThreadPool;
    }

    private static long GetPauseThreshold(long? maxBufferSize)
    {
        return maxBufferSize is > 0
            ? maxBufferSize.GetValueOrDefault()
            : 0;
    }

    private static long GetResumeThreshold(long? maxBufferSize)
    {
        if (maxBufferSize is not > 0)
        {
            return 0;
        }

        return Math.Max(1, maxBufferSize.GetValueOrDefault() / 2);
    }

    private static int ClampBufferSize(long? bufferSize, int defaultValue)
    {
        if (bufferSize is > 0 and <= int.MaxValue)
        {
            return (int)bufferSize.GetValueOrDefault();
        }

        return defaultValue;
    }
}
