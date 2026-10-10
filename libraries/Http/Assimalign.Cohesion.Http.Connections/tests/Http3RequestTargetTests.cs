using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The request-target forms of an HTTP/3 request (#1334): a classic CONNECT carries the host and port to
/// connect to in <c>:authority</c> and neither <c>:scheme</c> nor <c>:path</c> (RFC 9114 §4.4), and a
/// <c>:path</c> of <c>*</c> belongs to OPTIONS alone (RFC 9110 §7.1, RFC 9114 §4.3.1). A request that
/// breaks either rule is malformed: its stream is reset with <c>H3_MESSAGE_ERROR</c> before it reaches
/// the application (RFC 9114 §4.1.2), and the connection keeps serving its other streams. The rule is the
/// one HTTP/2 applies, and matches HTTP/1.1's authority-form and asterisk-form.
/// </summary>
public class Http3RequestTargetTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Targets: A classic CONNECT without a host and port, or with :scheme or :path, should reset only its stream")]
    [InlineData(null, null, null)]
    [InlineData(null, null, "")]
    [InlineData(null, null, "upstream.test")]
    [InlineData("https", null, "upstream.test:443")]
    [InlineData(null, "/", "upstream.test:443")]
    [InlineData("https", "/tunnel", "upstream.test:443")]
    public async Task ReceiveAsync_OnMalformedClassicConnect_ShouldResetStreamWithMessageError(string? scheme, string? path, string? authority)
    {
        // Arrange
        List<(string Name, string Value)> fields = [(":method", "CONNECT")];

        if (scheme is not null)
        {
            fields.Add((":scheme", scheme));
        }

        if (path is not null)
        {
            fields.Add((":path", path));
        }

        if (authority is not null)
        {
            fields.Add((":authority", authority));
        }

        // Act and Assert
        await AssertResetWhileSiblingIsServedAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(fields.ToArray()));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Targets: An asterisk-form :path outside OPTIONS, or one that is not exactly '*', should reset only its stream")]
    [InlineData("GET", "*")]
    [InlineData("POST", "*")]
    [InlineData("GET", "*foo")]
    [InlineData("OPTIONS", "*foo")]
    [InlineData("OPTIONS", "*?x=1")]
    public async Task ReceiveAsync_OnAsteriskFormOutsideOptions_ShouldResetStreamWithMessageError(string method, string path)
    {
        // Arrange
        byte[] malformed = HttpProtocolPayloadFactory.CreateHttp3Request(method, path, "https", "api.test");

        // Act and Assert
        await AssertResetWhileSiblingIsServedAsync(malformed);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Targets: An OPTIONS request in asterisk form should be dispatched")]
    public async Task ReceiveAsync_OnAsteriskFormOptions_ShouldDispatchRequest()
    {
        // Act
        IHttpContext context = await ReceiveSingleAsync(HttpProtocolPayloadFactory.CreateHttp3Request("OPTIONS", "*", "https", "api.test"));

        // Assert
        context.Request.Method.ShouldBe(HttpMethod.Options);
        context.Request.Path.Value.ShouldBe("*");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Targets: A classic CONNECT with a host and port should be dispatched")]
    public async Task ReceiveAsync_OnClassicConnect_ShouldDispatchRequest()
    {
        // Act
        IHttpContext context = await ReceiveSingleAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":authority", "upstream.test:443")));

        // Assert
        context.Request.Method.ShouldBe(HttpMethod.Connect);
        context.Request.Host.Value.ShouldBe("upstream.test:443");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Request Targets: A valid extended CONNECT should still be dispatched")]
    public async Task ReceiveAsync_OnExtendedConnect_ShouldDispatchRequest()
    {
        // Act — RFC 9220 §3: an extended CONNECT carries :scheme and :path, which the classic rule forbids
        // only when :protocol is absent.
        IHttpContext context = await ReceiveSingleAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test")));

        // Assert
        context.Request.Method.ShouldBe(HttpMethod.Connect);
        context.Request.Path.Value.ShouldBe("/chat");
    }

    private static async Task AssertResetWhileSiblingIsServedAsync(byte[] malformedPayload)
    {
        TestConnection malformed = new(malformedPayload);
        TestConnection sibling = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/ok", "https", "api.test"));
        TestMultiplexedConnection connection = new(malformed, sibling);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();

        // The malformed stream never became an exchange; the sibling is the first dispatched.
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Request.Path.Value.ShouldBe("/ok");
        malformed.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    private static async Task<IHttpContext> ReceiveSingleAsync(byte[] payload)
    {
        TestMultiplexedConnection connection = new(new TestConnection(payload));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(connection));

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }
}
