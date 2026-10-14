using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Udp.Tests;

public class UdpOptionsTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Bind ReceiveBufferSize: Reject negative assignments")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void BindReceiveBufferSize_WithNegativeValue_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        UdpBindOptions options = new() { ReceiveBufferSize = 1024 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.ReceiveBufferSize = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.ReceiveBufferSize.ShouldBe(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Bind SendBufferSize: Reject negative assignments")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void BindSendBufferSize_WithNegativeValue_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        UdpBindOptions options = new() { SendBufferSize = 1024 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.SendBufferSize = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.SendBufferSize.ShouldBe(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Bind buffer sizes: Preserve null, zero and positive sizes")]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void BindBufferSizes_WithSupportedValue_ShouldPreserveValue(int? value)
    {
        // Arrange
        UdpBindOptions options = new() { ReceiveBufferSize = 1024, SendBufferSize = 1024 };

        // Act
        options.ReceiveBufferSize = value;
        options.SendBufferSize = value;

        // Assert
        options.ReceiveBufferSize.ShouldBe(value);
        options.SendBufferSize.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Connect ReceiveBufferSize: Reject negative assignments")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ConnectReceiveBufferSize_WithNegativeValue_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        UdpConnectOptions options = new() { ReceiveBufferSize = 1024 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.ReceiveBufferSize = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.ReceiveBufferSize.ShouldBe(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Connect SendBufferSize: Reject negative assignments")]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ConnectSendBufferSize_WithNegativeValue_ShouldThrowAndPreservePreviousValue(int value)
    {
        // Arrange
        UdpConnectOptions options = new() { SendBufferSize = 1024 };

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.SendBufferSize = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
        options.SendBufferSize.ShouldBe(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Udp] - Connect buffer sizes: Preserve null, zero and positive sizes")]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void ConnectBufferSizes_WithSupportedValue_ShouldPreserveValue(int? value)
    {
        // Arrange
        UdpConnectOptions options = new() { ReceiveBufferSize = 1024, SendBufferSize = 1024 };

        // Act
        options.ReceiveBufferSize = value;
        options.SendBufferSize = value;

        // Assert
        options.ReceiveBufferSize.ShouldBe(value);
        options.SendBufferSize.ShouldBe(value);
    }
}
