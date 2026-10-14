using System;
using System.Net.WebSockets;
using System.Threading;

using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

public class WebSocketOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Options: The defaults refuse cross-site handshakes, keep alive every 30 seconds and leave compression off")]
    public void Options_Defaults_AreSafe()
    {
        // Act
        WebSocketOptions options = new();

        // Assert
        options.AllowedOrigins.ShouldBeEmpty();
        options.AllowAnyOrigin.ShouldBeFalse();
        options.KeepAliveInterval.ShouldBe(WebSocket.DefaultKeepAliveInterval);
        options.KeepAliveTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
        options.DangerousEnableCompression.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Options: A negative keep-alive interval or timeout is rejected")]
    public void Options_NegativeKeepAlive_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        WebSocketOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.KeepAliveInterval = TimeSpan.FromMilliseconds(-5));
        Should.Throw<ArgumentOutOfRangeException>(() => options.KeepAliveTimeout = TimeSpan.FromMilliseconds(-5));
        options.KeepAliveInterval = TimeSpan.Zero;
        options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        options.KeepAliveInterval.ShouldBe(TimeSpan.Zero);
        options.KeepAliveTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: An allowed origin that is not a serialized origin fails when the middleware is added")]
    [InlineData("*")]
    [InlineData("null")]
    [InlineData("https://app.example/")]
    [InlineData("app.example")]
    public void UseWebSockets_InvalidAllowedOrigin_ThrowsArgumentException(string origin)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act / Assert
        Should.Throw<ArgumentException>(() => builder.UseWebSockets(options => options.AllowedOrigins.Add(origin)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A null builder throws")]
    public void UseWebSockets_NullBuilder_ThrowsArgumentNullException()
    {
        IWebApplicationPipelineBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() => builder.UseWebSockets());
    }
}
