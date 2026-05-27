using System.Buffers;
using System.IO.Pipelines;

using Assimalign.Cohesion.Transports.Internal;

using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TransportPipeOptionsFactoryTests
{
    [Fact]
    public void CreatePipeOptions_WhenBufferSizeIsConfigured_ShouldUseAdaptivePoolAndHonorThresholds()
    {
        // Arrange
        const long maxReadBufferSize = 8192;
        const long maxWriteBufferSize = 4096;

        // Act
        using TransportPipeOptionsContext options = TransportPipeOptionsFactory.CreatePipeOptions(
            maxReadBufferSize,
            maxWriteBufferSize,
            unsafePreferInLineScheduling: true);

        // Assert
        Assert.Equal(maxReadBufferSize, options.InputOptions.PauseWriterThreshold);
        Assert.Equal(maxReadBufferSize / 2, options.InputOptions.ResumeWriterThreshold);
        Assert.Equal(maxWriteBufferSize, options.OutputOptions.PauseWriterThreshold);
        Assert.Equal(maxWriteBufferSize / 2, options.OutputOptions.ResumeWriterThreshold);
        Assert.Equal(4096, options.InputOptions.MinimumSegmentSize);
        Assert.Equal(4096, options.OutputOptions.MinimumSegmentSize);
        Assert.IsType<AdaptiveMemoryPool>(options.InputOptions.Pool);
        Assert.Same(options.InputOptions.Pool, options.OutputOptions.Pool);
        Assert.Same(PipeScheduler.Inline, options.InputOptions.ReaderScheduler);
        Assert.Same(PipeScheduler.Inline, options.InputOptions.WriterScheduler);
        Assert.False(options.InputOptions.UseSynchronizationContext);
        Assert.False(options.OutputOptions.UseSynchronizationContext);
    }

    [Fact]
    public void CreatePipeOptions_WhenBufferSizeIsNotConfigured_ShouldUseRuntimeDefaultThresholds()
    {
        // Act
        using TransportPipeOptionsContext options = TransportPipeOptionsFactory.CreatePipeOptions(
            null,
            null,
            unsafePreferInLineScheduling: false);

        // Assert
        Assert.Equal(0, options.InputOptions.PauseWriterThreshold);
        Assert.Equal(1, options.InputOptions.ResumeWriterThreshold);
        Assert.Equal(0, options.OutputOptions.PauseWriterThreshold);
        Assert.Equal(1, options.OutputOptions.ResumeWriterThreshold);
        Assert.IsType<AdaptiveMemoryPool>(options.InputOptions.Pool);
        Assert.Same(PipeScheduler.ThreadPool, options.InputOptions.ReaderScheduler);
        Assert.Same(PipeScheduler.ThreadPool, options.InputOptions.WriterScheduler);
    }

    [Fact]
    public void CreateStreamOptions_WhenBufferSizeIsNotValid_ShouldUseDefaultReadBufferSizeAndAdaptivePool()
    {
        // Act
        using TransportStreamPipeOptionsContext options = TransportPipeOptionsFactory.CreateStreamOptions(long.MaxValue, null);

        // Assert
        Assert.Equal(64 * 1024, options.ReaderOptions.BufferSize);
        Assert.Equal(4096, options.ReaderOptions.MinimumReadSize);
        Assert.Equal(16 * 1024, options.WriterOptions.MinimumBufferSize);
        Assert.IsType<AdaptiveMemoryPool>(options.ReaderOptions.Pool);
        Assert.Same(options.ReaderOptions.Pool, options.WriterOptions.Pool);
        Assert.False(options.ReaderOptions.LeaveOpen);
        Assert.False(options.WriterOptions.LeaveOpen);
    }

    [Fact]
    public void CreateStreamOptions_WhenBufferSizeIsConfigured_ShouldUseConfiguredMinimumBuffer()
    {
        // Arrange
        const long maxBufferSize = 2048;

        // Act
        using TransportStreamPipeOptionsContext options = TransportPipeOptionsFactory.CreateStreamOptions(null, maxBufferSize);

        // Assert
        Assert.Equal((int)maxBufferSize, options.WriterOptions.MinimumBufferSize);
    }
}
