using System;

using Assimalign.Cohesion.Connections.Tcp.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpOptionsTests
{
    [Fact]
    public void ServerOptions_WhenConstructed_ShouldUseDocumentedDefaults()
    {
        // Arrange / Act
        TcpConnectionListenerOptions options = new();

        // Assert
        options.Backlog.ShouldBe(512);
        options.NoDelay.ShouldBeTrue();
        options.MaxReadBufferSize.ShouldBe(1024 * 1024);
        options.MaxWriteBufferSize.ShouldBe(64 * 1024);
        options.WaitForDataBeforeAllocatingBuffer.ShouldBeTrue();
        options.UnsafePreferInLineScheduling.ShouldBeFalse();
        options.IOQueueCount.ShouldBe(Math.Min(Environment.ProcessorCount, 16));
    }

    [Fact]
    public void ClientOptions_WhenConstructed_ShouldUseDocumentedDefaults()
    {
        // Arrange / Act
        TcpConnectionFactoryOptions options = new();

        // Assert
        options.NoDelay.ShouldBeTrue();
        options.MaxReadBufferSize.ShouldBe(1024 * 1024);
        options.MaxWriteBufferSize.ShouldBe(64 * 1024);
        options.WaitForDataBeforeAllocatingBuffer.ShouldBeTrue();
        options.UnsafePreferInLineScheduling.ShouldBeFalse();
    }

    [Fact]
    public void CreateConnectionSettings_WithZeroIOQueueCount_ShouldClampToSingleSettingsSlot()
    {
        // Arrange
        TcpConnectionListenerOptions options = new()
        {
            IOQueueCount = 0
        };

        // Act
        TcpConnectionSettings[] settings = options.CreateConnectionSettings();

        try
        {
            // Assert
            settings.Length.ShouldBe(1);
            settings[0].PipeOptions.ShouldNotBeNull();
        }
        finally
        {
            DisposeSettings(settings);
        }
    }

    [Fact]
    public void CreateConnectionSettings_WithPositiveIOQueueCount_ShouldCreateOneSlotPerQueue()
    {
        // Arrange
        TcpConnectionListenerOptions options = new()
        {
            IOQueueCount = 3,
            WaitForDataBeforeAllocatingBuffer = false
        };

        // Act
        TcpConnectionSettings[] settings = options.CreateConnectionSettings();

        try
        {
            // Assert
            settings.Length.ShouldBe(3);

            foreach (TcpConnectionSettings setting in settings)
            {
                setting.PipeOptions.ShouldNotBeNull();
                setting.WaitForDataBeforeAllocatingBuffer.ShouldBeFalse();
            }
        }
        finally
        {
            DisposeSettings(settings);
        }
    }

    private static void DisposeSettings(TcpConnectionSettings[] settings)
    {
        foreach (TcpConnectionSettings setting in settings)
        {
            setting.PipeOptions.Dispose();
        }
    }
}
