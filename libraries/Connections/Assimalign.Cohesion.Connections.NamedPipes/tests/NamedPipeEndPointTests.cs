using System;
using System.Net.Sockets;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

public class NamedPipeEndPointTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - EndPoint: Should default the server name to the local host")]
    public void Constructor_WithPipeNameOnly_ShouldDefaultToLocalServer()
    {
        // Arrange / Act
        NamedPipeEndPoint endPoint = new("orders");

        // Assert
        endPoint.PipeName.ShouldBe("orders");
        endPoint.ServerName.ShouldBe(".");
        endPoint.IsLocal.ShouldBeTrue();
        endPoint.AddressFamily.ShouldBe(AddressFamily.Unspecified);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - EndPoint: Should format as a Windows pipe path")]
    public void ToString_ShouldFormatAsWindowsPipePath()
    {
        // Arrange
        NamedPipeEndPoint endPoint = new("orders", "build-server");

        // Act / Assert
        endPoint.ToString().ShouldBe(@"\\build-server\pipe\orders");
        endPoint.IsLocal.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - EndPoint: Should compare case-insensitively")]
    public void Equals_WithDifferentCasing_ShouldBeEqual()
    {
        // Arrange
        NamedPipeEndPoint a = new("Orders");
        NamedPipeEndPoint b = new("orders", ".");

        // Act / Assert
        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Theory(DisplayName = "Cohesion Test [Connections.NamedPipes] - EndPoint: Should reject null or empty names")]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_WithInvalidPipeName_ShouldThrowArgumentException(string? pipeName)
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => new NamedPipeEndPoint(pipeName!));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - EndPoint: Should reject an empty server name")]
    public void Constructor_WithEmptyServerName_ShouldThrowArgumentException()
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => new NamedPipeEndPoint("orders", ""));
    }
}
