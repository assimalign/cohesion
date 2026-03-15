using System.Buffers;
using System.IO.Pipelines;

using Assimalign.Cohesion.Transports.Internal;

using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TransportPipeOptionsFactoryTests
{
    [Fact]
    public void CreateInputOptions_WhenBufferSizeIsConfigured_ShouldHonorThresholdsAndSchedulers()
    {
        // Arrange
        const long maxBufferSize = 8192;

        // Act
        PipeOptions options = TransportPipeOptionsFactory.CreateInputOptions(maxBufferSize, unsafePreferInLineScheduling: true);

        // Assert
        Assert.Equal(maxBufferSize, options.PauseWriterThreshold);
        Assert.Equal(maxBufferSize / 2, options.ResumeWriterThreshold);
        Assert.Equal(4096, options.MinimumSegmentSize);
        Assert.Same(MemoryPool<byte>.Shared, options.Pool);
        Assert.Same(PipeScheduler.Inline, options.ReaderScheduler);
        Assert.Same(PipeScheduler.Inline, options.WriterScheduler);
        Assert.False(options.UseSynchronizationContext);
    }

    [Fact]
    public void CreateOutputOptions_WhenBufferSizeIsNotConfigured_ShouldUseRuntimeDefaultThresholds()
    {
        // Act
        PipeOptions options = TransportPipeOptionsFactory.CreateOutputOptions(null, unsafePreferInLineScheduling: false);

        // Assert
        Assert.Equal(0, options.PauseWriterThreshold);
        Assert.Equal(1, options.ResumeWriterThreshold);
        Assert.Equal(4096, options.MinimumSegmentSize);
        Assert.Same(MemoryPool<byte>.Shared, options.Pool);
        Assert.Same(PipeScheduler.ThreadPool, options.ReaderScheduler);
        Assert.Same(PipeScheduler.ThreadPool, options.WriterScheduler);
        Assert.False(options.UseSynchronizationContext);
    }

    [Fact]
    public void CreateReaderOptions_WhenBufferSizeIsNotValid_ShouldUseDefaultReadBufferSize()
    {
        // Act
        StreamPipeReaderOptions options = TransportPipeOptionsFactory.CreateReaderOptions(long.MaxValue);

        // Assert
        Assert.Equal(64 * 1024, options.BufferSize);
        Assert.Equal(4096, options.MinimumReadSize);
        Assert.Same(MemoryPool<byte>.Shared, options.Pool);
        Assert.False(options.LeaveOpen);
    }

    [Fact]
    public void CreateWriterOptions_WhenBufferSizeIsConfigured_ShouldUseConfiguredMinimumBuffer()
    {
        // Arrange
        const long maxBufferSize = 2048;

        // Act
        StreamPipeWriterOptions options = TransportPipeOptionsFactory.CreateWriterOptions(maxBufferSize);

        // Assert
        Assert.Equal((int)maxBufferSize, options.MinimumBufferSize);
        Assert.Same(MemoryPool<byte>.Shared, options.Pool);
        Assert.False(options.LeaveOpen);
    }
}
