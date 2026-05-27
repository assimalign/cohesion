using System.Buffers;

using Assimalign.Cohesion.Transports.Internal;

using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TcpTransportConnectionSettingsTests
{
    [Fact]
    public void CreateConnectionSettings_WhenCreatedFromTcpOptions_ShouldUseAdaptiveMemoryPool()
    {
        // Arrange
        TcpServerTransportOptions options = new TcpServerTransportOptions()
        {
            IOQueueCount = 1,
            MaxReadBufferSize = 128 * 1024,
            MaxWriteBufferSize = 64 * 1024
        };

        // Act
        TcpTransportConnectionSettings[] settings = options.CreateConnectionSettings();

        AdaptiveMemoryPool inputPool = Assert.IsType<AdaptiveMemoryPool>(settings[0].PipeOptions.InputOptions.Pool);
        AdaptiveMemoryPool outputPool = Assert.IsType<AdaptiveMemoryPool>(settings[0].PipeOptions.OutputOptions.Pool);

        // Assert
        Assert.Same(inputPool, outputPool);
        Assert.Equal(AdaptiveMemoryPool.DefaultBlockSize, settings[0].PipeOptions.BlockSize);

        settings[0].PipeOptions.Dispose();
    }
}
