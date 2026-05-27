using System;
using System.Buffers;
using System.IO.Pipelines;

using Assimalign.Cohesion.Transports.Internal;

using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TransportPipeOptionsFactorySocketTests
{
    [Fact]
    public void CreateSocketConnectionSettings_WhenCreated_ShouldUseSocketSchedulersAndSharedAdaptivePool()
    {
        // Arrange
        const long maxReadBufferSize = 128 * 1024;
        const long maxWriteBufferSize = 64 * 1024;

        // Act
        TcpTransportConnectionSettings[] settings = TransportPipeOptionsFactory.CreateSocketConnectionSettings(
            1,
            unsafePreferInLineScheduling: false,
            maxReadBufferSize: maxReadBufferSize,
            maxWriteBufferSize: maxWriteBufferSize);

        try
        {
            AdaptiveMemoryPool inputPool = Assert.IsType<AdaptiveMemoryPool>(settings[0].PipeOptions.InputOptions.Pool);
            AdaptiveMemoryPool outputPool = Assert.IsType<AdaptiveMemoryPool>(settings[0].PipeOptions.OutputOptions.Pool);

            // Assert
            Assert.Same(inputPool, outputPool);
            Assert.IsType<SocketPipeScheduler>(settings[0].PipeOptions.ReceiverScheduler);

            if (OperatingSystem.IsWindows())
            {
                Assert.Same(settings[0].PipeOptions.ReceiverScheduler, settings[0].PipeOptions.SenderScheduler);
            }
            else
            {
                Assert.Same(PipeScheduler.Inline, settings[0].PipeOptions.SenderScheduler);
            }
        }
        finally
        {
            settings[0].PipeOptions.Dispose();
        }
    }

    [Fact]
    public void CreateSocketPipeOptions_WhenBufferSizeIsConfigured_ShouldUseAdaptivePoolAndHonorThresholds()
    {
        // Arrange
        const long maxReadBufferSize = 8192;
        const long maxWriteBufferSize = 4096;

        // Act
        using SocketTransportPipeOptionsContext options = TransportPipeOptionsFactory.CreateSocketPipeOptions(
            maxReadBufferSize,
            maxWriteBufferSize,
            unsafePreferInLineScheduling: true);

        // Assert
        Assert.Equal(maxReadBufferSize, options.InputOptions.PauseWriterThreshold);
        Assert.Equal(maxReadBufferSize / 2, options.InputOptions.ResumeWriterThreshold);
        Assert.Equal(maxWriteBufferSize, options.OutputOptions.PauseWriterThreshold);
        Assert.Equal(maxWriteBufferSize / 2, options.OutputOptions.ResumeWriterThreshold);
        Assert.Same(PipeScheduler.Inline, options.ReceiverScheduler);
        Assert.Same(PipeScheduler.Inline, options.SenderScheduler);
        Assert.IsType<AdaptiveMemoryPool>(options.InputOptions.Pool);
        Assert.Same(options.InputOptions.Pool, options.OutputOptions.Pool);
    }
}
