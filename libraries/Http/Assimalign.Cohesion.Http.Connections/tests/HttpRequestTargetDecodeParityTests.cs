using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Cross-transport parity for request-target percent-decoding (issue #895 / RFC 3986 §2.4). The
/// HTTP/1.1 reader now surfaces <see cref="IHttpRequest.Path"/> from the origin-form request-target
/// through the same <c>HttpPath.FromUriComponent</c> decode HTTP/2 and HTTP/3 run over the
/// <c>:path</c> pseudo-header (<c>Http2Stream.ParseQuery</c> / <c>Http3HeaderCodec.ParseQuery</c>),
/// so identical wire bytes yield an identical decoded path on every transport: <c>%2e%2e</c> decodes
/// to <c>..</c>, <c>%2F</c> stays encoded (never a separator — the routing <c>{**}</c> identity
/// invariant), ordinary octets decode, and invalid/overlong escapes are left intact per
/// <c>UrlDecoder</c>. Query text is split off before the decode and parsed identically on both
/// transports (<c>HttpQuery.Parse</c>), so it decodes fully — including <c>%2F</c> — on both.
/// </summary>
public class HttpRequestTargetDecodeParityTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Decode parity: h1 and h2 surface IHttpRequest.Path identically from an encoded request-target")]
    [InlineData("/static/%2e%2e/x", "/static/../x")]        // encoded dot segments decode to ".."
    [InlineData("/static/%2E%2E/x", "/static/../x")]        // percent-encoding hex is case-insensitive
    [InlineData("/dir/a%2Fb", "/dir/a%2Fb")]                // %2F stays encoded (never a separator)
    [InlineData("/dir/a%2fb", "/dir/a%2fb")]                // lowercase %2f is preserved too
    [InlineData("/hello%24world", "/hello$world")]          // an ordinary encoded octet decodes
    [InlineData("/bad%zz", "/bad%zz")]                      // an invalid escape is left unencoded
    [InlineData("/over%C0%AE", "/over%C0%AE")]              // an overlong UTF-8 sequence is left unencoded
    [InlineData("/plain/path", "/plain/path")]              // no percent-encoding — unchanged
    public async Task RequestTargetPath_DecodesIdenticallyAcrossHttp1AndHttp2(string rawTarget, string expectedPath)
    {
        // Act — the identical wire bytes as an h1 origin-form request-target and an h2 :path.
        IHttpContext? http1 = await ReadHttp1ContextAsync($"GET {rawTarget} HTTP/1.1\r\nHost: api.test\r\n\r\n");
        IHttpContext? http2 = await ReadHttp2ContextAsync(rawTarget);

        // Assert — both transports produced a context, and both decoded the path to the same text.
        http1.ShouldNotBeNull();
        http2.ShouldNotBeNull();
        http1!.Request.Path.Value.ShouldBe(expectedPath);
        http2!.Request.Path.Value.ShouldBe(expectedPath);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Decode parity: h1 and h2 split the query on the first '?' and decode it identically")]
    public async Task RequestTargetQuery_SplitsOnFirstQuestionMarkAndDecodesIdentically()
    {
        // Arrange — the path component keeps %2F encoded; the query component decodes fully (including
        // %2F → '/') because the query goes through HttpQuery.Parse, not HttpPath.FromUriComponent.
        const string rawTarget = "/items%2Fa?q=a%20b&p=%2F";

        // Act
        IHttpContext? http1 = await ReadHttp1ContextAsync($"GET {rawTarget} HTTP/1.1\r\nHost: api.test\r\n\r\n");
        IHttpContext? http2 = await ReadHttp2ContextAsync(rawTarget);

        // Assert
        http1.ShouldNotBeNull();
        http2.ShouldNotBeNull();

        http1!.Request.Path.Value.ShouldBe("/items%2Fa");
        http2!.Request.Path.Value.ShouldBe("/items%2Fa");

        http1.Request.Query["q"].Value.ShouldBe("a b");
        http2.Request.Query["q"].Value.ShouldBe("a b");
        http1.Request.Query["p"].Value.ShouldBe("/");
        http2.Request.Query["p"].Value.ShouldBe("/");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1: a request-target whose decoded path holds an illegal character is rejected as malformed")]
    [InlineData("/space%20name")]  // a decoded space is not a legal path character
    [InlineData("/nul%00byte")]    // a decoded NUL is rejected
    [InlineData("/tab%09stop")]    // a decoded tab is not a legal path character
    public async Task Http1_RequestTargetPathDecodingToIllegalCharacter_DropsTheConnection(string rawTarget)
    {
        // A decoded octet that HttpPath rejects makes the request-target malformed — the same throw the
        // shared HttpPath.FromUriComponent decode reaches on h2/h3. The h1 reader surfaces it as its
        // existing malformed-request-target failure, so the connection is dropped with no context (it is
        // never mistaken for a literal, reachable path).
        IHttpContext? context = await ReadHttp1ContextAsync($"GET {rawTarget} HTTP/1.1\r\nHost: api.test\r\n\r\n");

        context.ShouldBeNull();
    }

    private static async Task<IHttpContext?> ReadHttp1ContextAsync(string requestText)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(requestText);
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        return await ReadFirstContextOrNullAsync(context);
    }

    private static async Task<IHttpContext?> ReadHttp2ContextAsync(string rawPath)
    {
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", rawPath, "https", "api.test");
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();
        return await ReadFirstContextOrNullAsync(context);
    }

    private static async Task<IHttpContext?> ReadFirstContextOrNullAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        return await enumerator.MoveNextAsync() ? enumerator.Current : null;
    }
}
