using System;
using System.Buffers;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections;

/// <summary>Creates pooled pipe options for connection drivers.</summary>
// Deviates from the repo namespace-matches-assembly rule per design decision: this file is
// shared source (CohesionSharedSource), compiled into each transport driver that needs pooled
// pipe options. It holds no mutable state - only constants and two readonly TimeSpans - and
// hands every object it creates to the calling assembly, so linking a private copy is safe.
internal static partial class PipeOptionsFactory
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

    public static PipeOptionsContext CreatePipeOptions(
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

    /// <summary>Creates stream adapter options backed by one owned adaptive memory pool.</summary>
    /// <param name="readBufferSize">Reader buffer size in bytes; null, nonpositive, or values above <see cref="int.MaxValue"/> use the default.</param>
    /// <param name="writeBufferSize">Writer buffer size in bytes; null, nonpositive, or values above <see cref="int.MaxValue"/> use the default.</param>
    /// <returns>A context that must outlive all stream adapters created from its options.</returns>
    public static StreamPipeOptionsContext CreateStreamOptions(long? readBufferSize, long? writeBufferSize)
    {
        AdaptiveMemoryPool memoryPool = CreateMemoryPool(readBufferSize, writeBufferSize);
        int configuredReadBufferSize = ClampBufferSize(readBufferSize, DefaultReadBufferSize);
        int configuredWriteBufferSize = ClampBufferSize(writeBufferSize, DefaultWriteBufferSize);

        return new StreamPipeOptionsContext(
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

    /// <summary>Creates receive and send pipe options with explicit continuation schedulers.</summary>
    /// <param name="maxReadBufferSize">Receive back-pressure threshold in bytes; null or nonpositive disables the threshold.</param>
    /// <param name="maxWriteBufferSize">Send back-pressure threshold in bytes; null or nonpositive disables the threshold.</param>
    /// <param name="applicationScheduler">Scheduler for application-side continuations.</param>
    /// <param name="transportScheduler">Scheduler for driver-side continuations.</param>
    /// <returns>A context that owns the shared pool and must outlive all pipes using the options.</returns>
    /// <exception cref="ArgumentNullException">Either scheduler is null.</exception>
    public static PipeOptionsContext CreatePipeOptions(
        long? maxReadBufferSize,
        long? maxWriteBufferSize,
        PipeScheduler applicationScheduler,
        PipeScheduler transportScheduler)
    {
        ArgumentNullException.ThrowIfNull(applicationScheduler);
        ArgumentNullException.ThrowIfNull(transportScheduler);

        AdaptiveMemoryPool memoryPool = CreateMemoryPool(maxReadBufferSize, maxWriteBufferSize);

        return new PipeOptionsContext(
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
