using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        // Act
        var exception = new MapperException("boom");

        // Assert
        exception.Message.ShouldBe("boom");
    }

    [Fact]
    public void Constructor_WithMessageAndInner_SetsBoth()
    {
        // Arrange
        var inner = new InvalidOperationException("inner");

        // Act
        var exception = new MapperException("outer", inner);

        // Assert
        exception.Message.ShouldBe("outer");
        exception.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void MapperException_IsAnException()
    {
        // Act / Assert
        new MapperException("x").ShouldBeAssignableTo<Exception>();
    }
}
