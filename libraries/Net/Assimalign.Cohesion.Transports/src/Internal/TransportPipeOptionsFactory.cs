using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports.Internal;

internal static class TransportPipeOptionsFactory
{
    private const int DefaultReadBufferSize = 64 * 1024;
    private const int DefaultWriteBufferSize = 16 * 1024;
    private const int DefaultMinimumRetainedBlocks = 32;
    private const int DefaultMaximumRetainedBlocks = 256;
    private const int MinimumSegmentSize = 4096;
    private static readonly TimeSpan defaultWarmWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan defaultTrimInterval = TimeSpan.FromSeconds(30);

    public static AdaptiveMemoryPool CreateMemoryPool(long? maxReadBufferSize, long? maxWriteBufferSize)
    {
        int maximumRetainedBlocks = GetMaximumRetainedBlocks(maxReadBufferSize, maxWriteBufferSize);

        return new AdaptiveMemoryPool(new AdaptiveMemoryPoolOptions()
        {
            BlockSize = AdaptiveMemoryPool.DefaultBlockSize,
            TrimInterval = defaultTrimInterval,
            Policy = new AdaptiveMemoryPoolPressurePolicy()
            {
                MinimumRetainedBlocks = Math.Min(DefaultMinimumRetainedBlocks, maximumRetainedBlocks),
                MaximumRetainedBlocks = maximumRetainedBlocks,
                PeakRetentionRatio = 0.25,
                WarmWindow = defaultWarmWindow
            }
        });
    }

    public static SocketTransportConnectionSettings[] CreateSocketConnectionSettings(
        int count,
        bool unsafePreferInLineScheduling = false,
        bool waitForDataBeforeAllocatingBuffer = false,
        long? maxReadBufferSize = 0,
        long? maxWriteBufferSize = 0)
    {
        var settings = new SocketTransportConnectionSettings[count];

        for (int index = 0; index < count; index++)
        {
            settings[index] = new SocketTransportConnectionSettings()
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

    public static TransportPipeOptionsContext CreatePipeOptions(
        long? maxReadBufferSize,
        long? maxWriteBufferSize,
        bool unsafePreferInLineScheduling)
    {
        PipeScheduler applicationScheduler = GetApplicationScheduler(unsafePreferInLineScheduling);
        PipeScheduler transportScheduler = GetTransportScheduler(unsafePreferInLineScheduling);

        return CreatePipeOptions(
            maxReadBufferSize,
            maxWriteBufferSize,
            applicationScheduler,
            transportScheduler);
    }

    public static SocketTransportPipeOptionsContext CreateSocketPipeOptions(
        long? maxReadBufferSize,
        long? maxWriteBufferSize,
        bool unsafePreferInLineScheduling)
    {
        PipeScheduler applicationScheduler = GetApplicationScheduler(unsafePreferInLineScheduling);
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

    public static TransportStreamPipeOptionsContext CreateStreamOptions(long? readBufferSize, long? writeBufferSize)
    {
        AdaptiveMemoryPool memoryPool = CreateMemoryPool(readBufferSize, writeBufferSize);
        int configuredReadBufferSize = ClampBufferSize(readBufferSize, DefaultReadBufferSize);
        int configuredWriteBufferSize = ClampBufferSize(writeBufferSize, DefaultWriteBufferSize);

        return new TransportStreamPipeOptionsContext(
            memoryPool,
            new StreamPipeReaderOptions(
                memoryPool,
                configuredReadBufferSize,
                Math.Min(configuredReadBufferSize, MinimumSegmentSize),
                leaveOpen: false),
            new StreamPipeWriterOptions(
                memoryPool,
                configuredWriteBufferSize,
                leaveOpen: false));
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

    private static PipeScheduler GetSocketTransportScheduler(bool unsafePreferInLineScheduling)
    {
        return unsafePreferInLineScheduling
            ? PipeScheduler.Inline
            : new SocketPipeScheduler();
    }

    private static TransportPipeOptionsContext CreatePipeOptions(
        long? maxReadBufferSize,
        long? maxWriteBufferSize,
        PipeScheduler applicationScheduler,
        PipeScheduler transportScheduler)
    {
        AdaptiveMemoryPool memoryPool = CreateMemoryPool(maxReadBufferSize, maxWriteBufferSize);

        return new TransportPipeOptionsContext(
            memoryPool,
            new PipeOptions(
                memoryPool,
                applicationScheduler,
                transportScheduler,
                GetPauseThreshold(maxReadBufferSize),
                GetResumeThreshold(maxReadBufferSize),
                MinimumSegmentSize,
                useSynchronizationContext: false),
            new PipeOptions(
                memoryPool,
                transportScheduler,
                applicationScheduler,
                GetPauseThreshold(maxWriteBufferSize),
                GetResumeThreshold(maxWriteBufferSize),
                MinimumSegmentSize,
                useSynchronizationContext: false));
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

    private static int GetMaximumRetainedBlocks(long? maxReadBufferSize, long? maxWriteBufferSize)
    {
        long totalBufferedBytes = GetPositiveBufferSize(maxReadBufferSize) + GetPositiveBufferSize(maxWriteBufferSize);

        if (totalBufferedBytes <= 0)
        {
            return DefaultMaximumRetainedBlocks;
        }

        long retainedBlockCount = (totalBufferedBytes + AdaptiveMemoryPool.DefaultBlockSize - 1) / AdaptiveMemoryPool.DefaultBlockSize;

        return (int)Math.Clamp(retainedBlockCount, DefaultMinimumRetainedBlocks, DefaultMaximumRetainedBlocks);
    }

    private static long GetPositiveBufferSize(long? bufferSize)
    {
        return bufferSize is > 0
            ? bufferSize.GetValueOrDefault()
            : 0;
    }
}
