using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHeaderKeyTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - SecWebSocketProtocol: Should map to the RFC 6455 header name")]
    public void SecWebSocketProtocol_Value_ShouldMatchRfc6455HeaderName()
    {
        // Arrange & Act
        string headerName = HttpHeaderKey.SecWebSocketProtocol.Value;

        // Assert
        headerName.ShouldBe("Sec-WebSocket-Protocol");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SecWebSocketProtocol: Should round-trip a value through IHttpHeaderCollection")]
    public void SecWebSocketProtocol_RoundTripThroughHeaderCollection_ShouldPreserveValue()
    {
        // Arrange
        IHttpHeaderCollection headers = new HttpHeaderCollection();
        headers.Add(HttpHeaderKey.SecWebSocketProtocol, "chat, superchat");

        // Act
        bool found = headers.TryGetValue(HttpHeaderKey.SecWebSocketProtocol, out HttpHeaderValue value);

        // Assert
        found.ShouldBeTrue();
        value.Value.ShouldBe("chat, superchat");
        headers[HttpHeaderKey.SecWebSocketProtocol].Value.ShouldBe("chat, superchat");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SecWebSocketProtocol: Should match case-insensitively through IHttpHeaderCollection")]
    public void SecWebSocketProtocol_LookupWithDifferentCasing_ShouldMatchCaseInsensitively()
    {
        // Arrange
        IHttpHeaderCollection headers = new HttpHeaderCollection();
        headers.Add(HttpHeaderKey.SecWebSocketProtocol, "chat");

        HttpHeaderKey lowerCaseKey = "sec-websocket-protocol";
        HttpHeaderKey upperCaseKey = "SEC-WEBSOCKET-PROTOCOL";

        // Act & Assert
        headers.ContainsKey(lowerCaseKey).ShouldBeTrue();
        headers.ContainsKey(upperCaseKey).ShouldBeTrue();

        headers.TryGetValue(lowerCaseKey, out HttpHeaderValue lowerValue).ShouldBeTrue();
        lowerValue.Value.ShouldBe("chat");

        headers[upperCaseKey].Value.ShouldBe("chat");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - SecWebSocketProtocol: Should enumerate with the canonical RFC key name")]
    public void SecWebSocketProtocol_EnumerateHeaderCollection_ShouldExposeCanonicalKeyName()
    {
        // Arrange
        IHttpHeaderCollection headers = new HttpHeaderCollection();
        headers.Add(HttpHeaderKey.SecWebSocketProtocol, "chat");

        // Act
        KeyValuePair<HttpHeaderKey, HttpHeaderValue> pair = headers.Single();

        // Assert
        pair.Key.Value.ShouldBe("Sec-WebSocket-Protocol");
        pair.Value.Value.ShouldBe("chat");
    }
}
