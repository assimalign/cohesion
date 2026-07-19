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

    [Fact(DisplayName = "Cohesion Test [Http] - StrictTransportSecurity: Should map to the RFC 6797 header name")]
    public void StrictTransportSecurity_Value_ShouldMatchRfc6797HeaderName()
    {
        // Arrange & Act
        string headerName = HttpHeaderKey.StrictTransportSecurity.Value;

        // Assert
        headerName.ShouldBe("Strict-Transport-Security");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - StrictTransportSecurity: Should round-trip a value through IHttpHeaderCollection")]
    public void StrictTransportSecurity_RoundTripThroughHeaderCollection_ShouldPreserveValue()
    {
        // Arrange
        IHttpHeaderCollection headers = new HttpHeaderCollection();
        headers.Add(HttpHeaderKey.StrictTransportSecurity, "max-age=31536000; includeSubDomains");

        // Act
        bool found = headers.TryGetValue(HttpHeaderKey.StrictTransportSecurity, out HttpHeaderValue value);

        // Assert
        found.ShouldBeTrue();
        value.Value.ShouldBe("max-age=31536000; includeSubDomains");
        headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000; includeSubDomains");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - StrictTransportSecurity: Should match case-insensitively through IHttpHeaderCollection")]
    public void StrictTransportSecurity_LookupWithWireCasing_ShouldMatchCaseInsensitively()
    {
        // Arrange
        IHttpHeaderCollection headers = new HttpHeaderCollection();
        headers.Add(HttpHeaderKey.StrictTransportSecurity, "max-age=0");

        HttpHeaderKey wireCaseKey = "strict-transport-security";

        // Act & Assert — the RFC 6797 wire name (lowercase) resolves the canonical key.
        headers.ContainsKey(wireCaseKey).ShouldBeTrue();
        headers.TryGetValue(wireCaseKey, out HttpHeaderValue value).ShouldBeTrue();
        value.Value.ShouldBe("max-age=0");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HeaderKey: Should expose the forwarding header names")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    public void ForwardingKeys_ShouldMapToCanonicalNames(string expected)
    {
        // Arrange
        var keysByName = new Dictionary<string, HttpHeaderKey>
        {
            ["Forwarded"] = HttpHeaderKey.Forwarded,
            ["X-Forwarded-For"] = HttpHeaderKey.XForwardedFor,
            ["X-Forwarded-Host"] = HttpHeaderKey.XForwardedHost,
            ["X-Forwarded-Proto"] = HttpHeaderKey.XForwardedProto,
        };

        // Act & Assert
        keysByName[expected].Value.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HeaderKey: Should round-trip Forwarded through IHttpHeaderCollection")]
    public void Forwarded_RoundTripThroughHeaderCollection_ShouldMatchCaseInsensitively()
    {
        // Arrange
        IHttpHeaderCollection headers = new HttpHeaderCollection();
        headers.Add(HttpHeaderKey.Forwarded, "for=192.0.2.43;proto=https");

        // Act & Assert
        headers.ContainsKey("forwarded").ShouldBeTrue();
        headers.TryGetValue(HttpHeaderKey.Forwarded, out HttpHeaderValue value).ShouldBeTrue();
        value.Value.ShouldBe("for=192.0.2.43;proto=https");
    }
}
