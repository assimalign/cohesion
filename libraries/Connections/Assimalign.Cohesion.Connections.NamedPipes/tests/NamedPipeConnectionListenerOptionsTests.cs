using System;
using System.IO.Pipes;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

public class NamedPipeConnectionListenerOptionsTests
{
    [Theory(DisplayName = "Cohesion Test [Connections.NamedPipes] - MaxServerInstances: Should reject an invalid limit at assignment")]
    [InlineData(int.MinValue)]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(int.MaxValue)]
    public void MaxServerInstances_WithInvalidValue_ShouldThrowArgumentOutOfRangeException(int value)
    {
        // Arrange
        NamedPipeConnectionListenerOptions options = new();

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.MaxServerInstances = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        options.MaxServerInstances.ShouldBe(NamedPipeServerStream.MaxAllowedServerInstances);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.NamedPipes] - MaxServerInstances: Should preserve supported limits and the unlimited sentinel")]
    [InlineData(NamedPipeServerStream.MaxAllowedServerInstances)]
    [InlineData(1)]
    [InlineData(254)]
    public void MaxServerInstances_WithSupportedValue_ShouldRetainValue(int value)
    {
        // Arrange
        NamedPipeConnectionListenerOptions options = new();

        // Act
        options.MaxServerInstances = value;

        // Assert
        options.MaxServerInstances.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.NamedPipes] - InputBufferSize: Should reject a negative size at assignment")]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void InputBufferSize_WithNegativeValue_ShouldThrowArgumentOutOfRangeException(int value)
    {
        // Arrange
        NamedPipeConnectionListenerOptions options = new();

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.InputBufferSize = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        options.InputBufferSize.ShouldBe(NamedPipeConnectionListenerOptions.DefaultBufferSize);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.NamedPipes] - OutputBufferSize: Should reject a negative size at assignment")]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void OutputBufferSize_WithNegativeValue_ShouldThrowArgumentOutOfRangeException(int value)
    {
        // Arrange
        NamedPipeConnectionListenerOptions options = new();

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => options.OutputBufferSize = value);

        // Assert
        exception.ParamName.ShouldBe("value");
        options.OutputBufferSize.ShouldBe(NamedPipeConnectionListenerOptions.DefaultBufferSize);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.NamedPipes] - BufferSizes: Should preserve supported sizes and the operating-system default")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void BufferSizes_WithSupportedValue_ShouldRetainValue(int value)
    {
        // Arrange
        NamedPipeConnectionListenerOptions options = new();

        // Act
        options.InputBufferSize = value;
        options.OutputBufferSize = value;

        // Assert
        options.InputBufferSize.ShouldBe(value);
        options.OutputBufferSize.ShouldBe(value);
    }
}
