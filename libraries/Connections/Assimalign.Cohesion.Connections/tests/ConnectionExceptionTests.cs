using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class ConnectionExceptionTests
{
    [Fact]
    public void Ctor_OnConnectionAbortedException_ShouldDeriveFromConnectionExceptionWithDefaultMessage()
    {
        // Arrange & Act
        ConnectionAbortedException exception = new();

        // Assert
        exception.ShouldBeAssignableTo<ConnectionException>();
        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Ctor_OnConnectionResetException_ShouldDeriveFromConnectionExceptionWithDefaultMessage()
    {
        // Arrange & Act
        ConnectionResetException exception = new();

        // Assert
        exception.ShouldBeAssignableTo<ConnectionException>();
        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Ctor_WithMessageAndInnerException_ShouldPreserveBoth()
    {
        // Arrange
        InvalidOperationException inner = new("socket failed");

        // Act
        ConnectionException baseException = new("base failed", inner);
        ConnectionAbortedException aborted = new("aborted", inner);
        ConnectionResetException reset = new("reset", inner);

        // Assert
        baseException.Message.ShouldBe("base failed");
        baseException.InnerException.ShouldBeSameAs(inner);
        aborted.Message.ShouldBe("aborted");
        aborted.InnerException.ShouldBeSameAs(inner);
        reset.Message.ShouldBe("reset");
        reset.InnerException.ShouldBeSameAs(inner);
    }
}
