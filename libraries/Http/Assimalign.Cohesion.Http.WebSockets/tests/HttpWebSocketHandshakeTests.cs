using System.Collections.Generic;

using Assimalign.Cohesion.Http.Internal;
using Assimalign.Cohesion.Http.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.WebSockets.Tests;

public class HttpWebSocketHandshakeTests
{
    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - ComputeAcceptKey: The RFC 6455 §1.3 sample key yields the sample accept value")]
    public void ComputeAcceptKey_Rfc6455SampleKey_ReturnsSampleAcceptValue()
    {
        // Act
        string accept = HttpWebSocketHandshake.ComputeAcceptKey(WebSocketTestContext.SampleKey);

        // Assert
        accept.ShouldBe(WebSocketTestContext.SampleAccept);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - TryGetKey: A trimmed base64 encoding of 16 bytes is a valid key")]
    public void TryGetKey_Base64Of16Bytes_ReturnsTrimmedKey()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SecWebSocketKey] = "  " + WebSocketTestContext.SampleKey + " ";

        // Act
        bool valid = HttpWebSocketHandshake.TryGetKey(headers, out string key);

        // Assert
        valid.ShouldBeTrue();
        key.ShouldBe(WebSocketTestContext.SampleKey);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - TryGetKey: A key that is not the base64 encoding of 16 bytes is rejected")]
    [InlineData("")]
    [InlineData("dGhlIHNhbXBsZQ==")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("dGhlIHNhbXBs ZSBub25jZQ=")]
    [InlineData("dGhlIHNhbXBsZSBub25jZQ==\u00A0")]   // a no-break space is not trimmed (RFC 9110 §5.6.3)
    [InlineData("\u0085dGhlIHNhbXBsZSBub25jZQ==")]  // nor is a next-line octet
    public void TryGetKey_MalformedKey_ReturnsFalse(string value)
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SecWebSocketKey] = value;

        // Act
        bool valid = HttpWebSocketHandshake.TryGetKey(headers, out string key);

        // Assert
        valid.ShouldBeFalse();
        key.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - TryGetKey: A missing key, or one sent twice, is rejected")]
    public void TryGetKey_MissingOrRepeatedKey_ReturnsFalse()
    {
        // Arrange
        HttpHeaderCollection missing = new();
        HttpHeaderCollection repeated = new();
        repeated[HttpHeaderKey.SecWebSocketKey] = new HttpHeaderValue(new[] { WebSocketTestContext.SampleKey, WebSocketTestContext.SampleKey });

        // Act / Assert
        HttpWebSocketHandshake.TryGetKey(missing, out _).ShouldBeFalse();
        HttpWebSocketHandshake.TryGetKey(repeated, out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - HasSupportedVersion: Only a version list naming 13 is supported")]
    [InlineData("13", true)]
    [InlineData(" 13 ", true)]
    [InlineData("8, 13", true)]
    [InlineData("8", false)]
    [InlineData("12", false)]
    [InlineData("130", false)]
    [InlineData("", false)]
    [InlineData("13\u00A0", false)]       // a no-break space is not optional whitespace
    [InlineData("\u008513", false)]     // nor is a next-line octet
    [InlineData("8, 13\u00A0", false)]
    public void HasSupportedVersion_VersionList_ReportsWhetherVersion13IsListed(string value, bool expected)
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SecWebSocketVersion] = value;

        // Act / Assert
        HttpWebSocketHandshake.HasSupportedVersion(headers).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - HasSupportedVersion: A request without Sec-WebSocket-Version names no supported version")]
    public void HasSupportedVersion_MissingHeader_ReturnsFalse()
    {
        HttpWebSocketHandshake.HasSupportedVersion(new HttpHeaderCollection()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - TryGetRequestedProtocols: Subprotocols across field lines are read in order, empty elements skipped")]
    public void TryGetRequestedProtocols_SeveralLines_ReturnsProtocolsInOrder()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SecWebSocketProtocol] = new HttpHeaderValue(new[] { "chat, , superchat", " v2.json " });

        // Act
        bool valid = HttpWebSocketHandshake.TryGetRequestedProtocols(headers, out IReadOnlyList<string> protocols);

        // Assert
        valid.ShouldBeTrue();
        protocols.ShouldBe(new[] { "chat", "superchat", "v2.json" });
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - TryGetRequestedProtocols: No Sec-WebSocket-Protocol is an empty, valid list")]
    public void TryGetRequestedProtocols_MissingHeader_ReturnsEmpty()
    {
        // Act
        bool valid = HttpWebSocketHandshake.TryGetRequestedProtocols(new HttpHeaderCollection(), out IReadOnlyList<string> protocols);

        // Assert
        valid.ShouldBeTrue();
        protocols.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - TryGetRequestedProtocols: A subprotocol that is not a token makes the list malformed")]
    [InlineData("chat room")]
    [InlineData("chat, \"quoted\"")]
    [InlineData("chat;v=1")]
    [InlineData("chat/1")]
    [InlineData("chat\u00A0")]            // a no-break space stays in the element, which is then no token
    [InlineData("superchat, \u0085chat")]
    public void TryGetRequestedProtocols_NonTokenElement_ReturnsFalse(string value)
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SecWebSocketProtocol] = value;

        // Act
        bool valid = HttpWebSocketHandshake.TryGetRequestedProtocols(headers, out IReadOnlyList<string> protocols);

        // Assert
        valid.ShouldBeFalse();
        protocols.ShouldBeEmpty();
    }
}
