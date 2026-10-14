using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.WebSockets.Tests;

public class HttpWebSocketAcceptOptionsTests
{
    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - SubProtocol: Invalid tokens are refused at assignment")]
    [InlineData("")]
    [InlineData("chat room")]
    [InlineData("chat/1")]
    [InlineData("chat,other")]
    [InlineData("chat;other")]
    [InlineData("chat\r\n")]
    [InlineData("café")]
    public void SubProtocol_InvalidToken_ThrowsArgumentException(string value)
    {
        // Arrange
        HttpWebSocketAcceptOptions options = new() { SubProtocol = "chat" };

        // Act
        ArgumentException error = Should.Throw<ArgumentException>(() => options.SubProtocol = value);

        // Assert
        error.ParamName.ShouldBe(nameof(HttpWebSocketAcceptOptions.SubProtocol));
        options.SubProtocol.ShouldBe("chat");
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - SubProtocol: Tokens retain their case and null selects none")]
    [InlineData(null)]
    [InlineData("Chat")]
    [InlineData("chat.v2")]
    [InlineData("!#$%&'*+-.^_`|~09AZaz")]
    public void SubProtocol_ValidTokenOrNull_PreservesValue(string? value)
    {
        // Arrange
        HttpWebSocketAcceptOptions options = new();

        // Act
        options.SubProtocol = value;

        // Assert
        options.SubProtocol.ShouldBe(value);
    }
}
