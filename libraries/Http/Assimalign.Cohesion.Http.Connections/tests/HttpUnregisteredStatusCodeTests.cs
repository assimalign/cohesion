using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpUnregisteredStatusCodeTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Status codes: Every transport should send an unregistered final status without a reason phrase")]
    [InlineData(1, 299, false)]
    [InlineData(1, 418, false)]
    [InlineData(1, 599, false)]
    [InlineData(2, 299, false)]
    [InlineData(2, 418, false)]
    [InlineData(2, 599, false)]
    [InlineData(3, 299, false)]
    [InlineData(3, 418, false)]
    [InlineData(3, 599, false)]
    [InlineData(1, 299, true)]
    [InlineData(2, 299, true)]
    [InlineData(3, 299, true)]
    public async Task SendAsync_WithUnregisteredStatus_ShouldWriteCodeWithoutPhrase(int protocol, int status, bool streamed)
    {
        // Arrange — drive real exchanges, including the separate streamed-head paths.
        HttpConnectionListenerOptions options = new() { AddDateHeader = false };
        if (streamed)
        {
            options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        }
        byte[] request = protocol switch
        {
            1 => HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"),
            2 => HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/", "http", "api.test"),
            _ => HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test"),
        };
        TestConnection transport = new(request);
        switch (protocol)
        {
            case 1: options.UseHttp1(new TestConnectionListener(transport)); break;
            case 2: options.UseHttp2(new TestConnectionListener(transport)); break;
            default: options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(transport))); break;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(timeout.Token);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(timeout.Token);
        await using IAsyncEnumerator<IHttpContext> requests = connectionContext.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        (await requests.MoveNextAsync()).ShouldBeTrue();
        await using IHttpContext context = requests.Current;

        // Act
        context.Response.StatusCode = status;
        if (streamed)
        {
            await context.Response.Streaming.WriteAsync("body"u8.ToArray(), timeout.Token);
        }
        else
        {
            context.Response.Body = new MemoryStream("body"u8.ToArray());
        }
        await connectionContext.SendAsync(context, timeout.Token);
        byte[] output = await transport.ReadOutputAsync().WaitAsync(timeout.Token);

        // Assert — HTTP/1.1 retains the required SP after the three digits; H2/H3 carry digits only.
        string digits = status.ToString(CultureInfo.InvariantCulture);
        if (protocol == 1)
        {
            string wire = Encoding.ASCII.GetString(output);
            wire[..wire.IndexOf("\r\n", StringComparison.Ordinal)].ShouldBe("HTTP/1.1 " + digits + " ");
            wire.ShouldContain("body", Case.Sensitive);
        }
        else
        {
            IReadOnlyList<(long FrameType, byte[] Payload)> frames = protocol == 2
                ? HttpProtocolPayloadFactory.ParseHttp2Frames(output)
                : HttpProtocolPayloadFactory.ParseHttp3Frames(output);
            byte[] head = frames.Where(frame => frame.FrameType == 1).ShouldHaveSingleItem().Payload;
            List<(string Name, string Value)> fields = protocol == 2
                ? new HPackDecoder().DecodeFieldLines(head)
                : QPackFieldSectionDecoder.Decode(head, long.MaxValue);
            fields.Where(field => field.Name == ":status").ShouldHaveSingleItem().Value.ShouldBe(digits);
            fields.Where(field => field.Name.StartsWith(':')).ShouldHaveSingleItem();
            byte[] body = frames.Where(frame => frame.FrameType == 0).SelectMany(frame => frame.Payload).ToArray();
            Encoding.ASCII.GetString(body).ShouldBe("body");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Status codes: Interim writers should encode unregistered informational codes without a phrase")]
    [InlineData(104)]
    [InlineData(199)]
    public async Task WriteInterimResponseAsync_WithUnregisteredStatus_ShouldWriteCodeWithoutPhrase(int status)
    {
        // Arrange
        using MemoryStream output = new();
        HttpStatusCode statusCode = status;

        // Act
        HttpInterimResponseRules.ValidateInterimStatusCode(statusCode);
        await Http1MessageWriter.WriteInterimResponseAsync(output, statusCode, null, CancellationToken.None);
        List<(string Name, string Value)> http2 = new HPackDecoder().DecodeFieldLines(
            HPackEncoder.EncodeInterimResponseHeaders(statusCode, null));
        List<(string Name, string Value)> http3 = QPackFieldSectionDecoder.Decode(
            Http3HeaderCodec.EncodeInterimResponseHeaders(statusCode, null), long.MaxValue);

        // Assert — an interim has no body, Content-Length, or Date, only its status here.
        string digits = status.ToString(CultureInfo.InvariantCulture);
        Encoding.ASCII.GetString(output.ToArray()).ShouldBe("HTTP/1.1 " + digits + " \r\n\r\n");
        http2.ShouldHaveSingleItem().ShouldBe((":status", digits));
        http3.ShouldHaveSingleItem().ShouldBe((":status", digits));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Status codes: Unregistered informational codes should remain invalid as final responses")]
    [InlineData(104)]
    [InlineData(199)]
    public void EnsureFinalStatusCode_WithUnregisteredInformationalStatus_ShouldReject(int status)
    {
        // Arrange — the numeric 1xx guard already existed; #1414 makes these values constructible.
        HttpStatusCode statusCode = status;

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => HttpInterimResponseRules.EnsureFinalStatusCode(statusCode));
        Should.Throw<InvalidOperationException>(() => Http1MessageWriter.EncodeHead(statusCode, new HttpHeaderCollection()));
    }
}
