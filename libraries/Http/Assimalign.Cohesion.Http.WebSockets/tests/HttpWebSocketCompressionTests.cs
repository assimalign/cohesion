using System.Net.WebSockets;

using Assimalign.Cohesion.Http.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.WebSockets.Tests;

/// <summary>
/// The server side of permessage-deflate negotiation (RFC 7692): which offer is accepted, the
/// response element, and the BCL options the agreed parameters map onto.
/// </summary>
public class HttpWebSocketCompressionTests
{
    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A bare offer is accepted with the default parameters")]
    public void TryNegotiate_BareOffer_AcceptsWithDefaults()
    {
        // Act
        bool accepted = Negotiate("permessage-deflate", out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate");
        options.ShouldNotBeNull();
        options.ServerContextTakeover.ShouldBeTrue();
        options.ClientContextTakeover.ShouldBeTrue();
        options.ServerMaxWindowBits.ShouldBe(15);
        options.ClientMaxWindowBits.ShouldBe(15);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A browser's client_max_window_bits without a value is accepted and not echoed")]
    public void TryNegotiate_ClientMaxWindowBitsWithoutValue_DoesNotEchoIt()
    {
        // Act — the offer Chrome and the .NET client send.
        bool accepted = Negotiate("permessage-deflate; client_max_window_bits", out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate");
        options!.ClientMaxWindowBits.ShouldBe(15);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A client window hint from 9 to 15 is echoed and sizes the inflater")]
    public void TryNegotiate_ClientWindowHint_EchoesItAndUsesIt()
    {
        // Act
        bool accepted = Negotiate("permessage-deflate; client_max_window_bits=10", out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; client_max_window_bits=10");
        options!.ClientMaxWindowBits.ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A client window hint of 8 is ignored, since zlib reads it with a larger window")]
    public void TryNegotiate_EightBitClientWindowHint_IsIgnored()
    {
        // Act
        bool accepted = Negotiate("permessage-deflate; client_max_window_bits=8", out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate");
        options!.ClientMaxWindowBits.ShouldBe(15);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A requested server window is honored and answered")]
    public void TryNegotiate_ServerMaxWindowBits_AnswersWithTheRequestedWindow()
    {
        // Act
        bool accepted = Negotiate("permessage-deflate; server_max_window_bits=10", out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; server_max_window_bits=10");
        options!.ServerMaxWindowBits.ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: The server answers with the smaller of the requested window and its own limit")]
    public void TryNegotiate_ServerLimitBelowRequest_AnswersWithTheLimit()
    {
        // Act
        bool accepted = Negotiate("permessage-deflate; server_max_window_bits=12", out WebSocketDeflateOptions? options, out string? response, serverMaxWindowBits: 10);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; server_max_window_bits=10");
        options!.ServerMaxWindowBits.ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: Both no-context-takeover parameters are accepted and echoed")]
    public void TryNegotiate_NoContextTakeoverParameters_AcceptsAndEchoesBoth()
    {
        // Act
        bool accepted = Negotiate(
            "permessage-deflate; server_no_context_takeover; client_no_context_takeover",
            out WebSocketDeflateOptions? options,
            out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; server_no_context_takeover; client_no_context_takeover");
        options!.ServerContextTakeover.ShouldBeFalse();
        options.ClientContextTakeover.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: The server's own choices are announced even when the client did not ask")]
    public void TryNegotiate_ServerPreferences_AreAnnounced()
    {
        // Act
        bool accepted = Negotiate(
            "permessage-deflate",
            out WebSocketDeflateOptions? options,
            out string? response,
            disableServerContextTakeover: true,
            serverMaxWindowBits: 12);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; server_no_context_takeover; server_max_window_bits=12");
        options!.ServerContextTakeover.ShouldBeFalse();
        options.ServerMaxWindowBits.ShouldBe(12);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A quoted parameter value is unescaped and accepted")]
    public void TryNegotiate_QuotedValue_IsAccepted()
    {
        // Act
        bool accepted = Negotiate("permessage-deflate; client_max_window_bits=\"12\"", out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; client_max_window_bits=12");
        options!.ClientMaxWindowBits.ShouldBe(12);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: An offer the server cannot honor is declined")]
    [InlineData("permessage-deflate; server_max_window_bits=8")]
    [InlineData("permessage-deflate; unknown_parameter")]
    [InlineData("permessage-deflate; server_no_context_takeover; server_no_context_takeover")]
    [InlineData("permessage-deflate; client_max_window_bits; client_max_window_bits=10")]
    [InlineData("permessage-deflate; server_no_context_takeover=1")]
    [InlineData("permessage-deflate; server_max_window_bits")]
    [InlineData("permessage-deflate; server_max_window_bits=16")]
    [InlineData("permessage-deflate; server_max_window_bits=08")]
    [InlineData("permessage-deflate; client_max_window_bits=x")]
    [InlineData("permessage-deflate; client_max_window_bits=7")]
    [InlineData("permessage-deflate; client_max_window_bits=\"12")]
    [InlineData("permessage-deflate;")]
    [InlineData("x-webkit-deflate-frame")]
    [InlineData("")]
    public void TryNegotiate_UnacceptableOffer_IsDeclined(string offer)
    {
        // Act
        bool accepted = Negotiate(offer, out WebSocketDeflateOptions? options, out string? response);

        // Assert
        accepted.ShouldBeFalse();
        options.ShouldBeNull();
        response.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: A declined offer falls back to the client's next one")]
    public void TryNegotiate_DeclinedOfferWithFallback_AcceptsTheFallback()
    {
        // Act
        bool accepted = Negotiate(
            "permessage-deflate; server_max_window_bits=8, permessage-deflate; client_no_context_takeover",
            out WebSocketDeflateOptions? options,
            out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; client_no_context_takeover");
        options!.ServerMaxWindowBits.ShouldBe(15);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: Other extensions are skipped, and a comma inside a quoted value does not split them")]
    public void TryNegotiate_OtherExtensionsFirst_FindsPermessageDeflate()
    {
        // Act
        bool accepted = Negotiate(
            "x-custom; note=\"a, b\", x-webkit-deflate-frame, PerMessage-Deflate",
            out WebSocketDeflateOptions? options,
            out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate");
        options.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Compression: Offers spread over several field lines are read in order")]
    public void TryNegotiate_SeveralFieldLines_ReadsEveryLine()
    {
        // Act
        bool accepted = HttpWebSocketCompression.TryNegotiate(
            new HttpHeaderValue(new[] { "x-custom", "permessage-deflate; server_max_window_bits=11" }),
            disableServerContextTakeover: false,
            serverMaxWindowBits: 15,
            out WebSocketDeflateOptions? options,
            out string? response);

        // Assert
        accepted.ShouldBeTrue();
        response.ShouldBe("permessage-deflate; server_max_window_bits=11");
        options!.ServerMaxWindowBits.ShouldBe(11);
    }

    private static bool Negotiate(
        string offer,
        out WebSocketDeflateOptions? options,
        out string? response,
        bool disableServerContextTakeover = false,
        int serverMaxWindowBits = 15)
    {
        return HttpWebSocketCompression.TryNegotiate(
            new HttpHeaderValue(offer),
            disableServerContextTakeover,
            serverMaxWindowBits,
            out options,
            out response);
    }
}
