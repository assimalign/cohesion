using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// A chunked HTTP/1.1 request body is only ever decoded at the offsets its peer framed it at, and
/// only when no other parser could frame it differently.
/// <list type="bullet">
///   <item>
///     A read that stops inside the framing, cancelled by the application for instance, loses the
///     octets of the line it read so far, so neither a later read nor the keep-alive drain resumes it:
///     resuming would read the rest of the line as a line of its own, and the rest of a chunk-size
///     line <c>40</c> is <c>0</c>, a last chunk, behind which the chunk's data can read as a new
///     request.
///   </item>
///   <item>
///     A framing line ends only at CRLF, and a bare CR or bare LF in it fails the body as malformed
///     (RFC 9112 §2.2), as do chunk extensions that break their grammar (RFC 9112 §7.1.1). An
///     intermediary that ends the chunk-size line <c>2;\nxx</c> at its LF frames every later chunk
///     differently from a reader that keeps the LF inside the line.
///   </item>
/// </list>
/// </summary>
public class Http1ChunkedFramingAlignmentTests
{
    private const string SmuggledRequest = "GET /smuggled HTTP/1.1\r\nHost: a\r\n\r\n";

    /// <summary>
    /// What the peer sends before the application's read is cancelled, and what it sends after.
    /// </summary>
    public static TheoryData<string, string> InterruptedFraming => new()
    {
        // Inside the chunk-size line "40": the rest reads as a last chunk, and the chunk's data, from
        // its leading CRLF, as the end of the trailer section and then a request.
        { "4", "0\r\n" + ("\r\n" + SmuggledRequest).PadRight(0x40, 'x') + "\r\n0\r\n\r\n" },

        // Inside the trailer field line "X-A: 0": the rest reads as a last chunk, and X-A is lost.
        { "5\r\nhello\r\n0\r\nX-A: ", "0\r\nX-B: 1\r\n\r\n" },
    };

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: The drain should not resume a body whose read was cancelled inside its framing")]
    [MemberData(nameof(InterruptedFraming))]
    public async Task DrainAsync_OnReadCancelledInsideFraming_ShouldNotDrain(string sentBeforeCancel, string sentAfterCancel)
    {
        // Arrange
        Pipe peer = new();
        Http1RequestBodyStream body = await InterruptReadAsync(peer, sentBeforeCancel);
        await peer.Writer.WriteAsync(Encoding.ASCII.GetBytes(sentAfterCancel));
        await peer.Writer.CompleteAsync();

        // Act
        bool drained = await body.DrainAsync(CancellationToken.None);

        // Assert — the connection closes; the client did nothing wrong, so no status is latched.
        drained.ShouldBeFalse();
        body.IsMalformed.ShouldBeFalse();
        body.RejectedStatusCode.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: A read after one cancelled inside the framing should fail instead of resuming mid-line")]
    [MemberData(nameof(InterruptedFraming))]
    public async Task ReadAsync_AfterReadCancelledInsideFraming_ShouldFailInsteadOfResuming(string sentBeforeCancel, string sentAfterCancel)
    {
        // Arrange
        Pipe peer = new();
        Http1RequestBodyStream body = await InterruptReadAsync(peer, sentBeforeCancel);
        await peer.Writer.WriteAsync(Encoding.ASCII.GetBytes(sentAfterCancel));
        await peer.Writer.CompleteAsync();

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await body.ReadAsync(new byte[256]));

        // Assert — resuming would end the body early, as if it were whole.
        failure.ShouldBeOfType<IOException>();
        body.IsMalformed.ShouldBeFalse();
        body.RejectedStatusCode.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: The drain should resume a body whose read was cancelled inside a chunk's data")]
    public async Task DrainAsync_OnReadCancelledInsideChunkData_ShouldDrain()
    {
        // Arrange — the cancelled read was waiting for the rest of a chunk's data, which it had not
        // consumed, so the decoder knows exactly where it is.
        Pipe peer = new();
        Http1RequestBodyStream body = await InterruptReadAsync(peer, "5\r\nhe");
        await peer.Writer.WriteAsync(Encoding.ASCII.GetBytes("llo\r\n0\r\n\r\n"));
        await peer.Writer.CompleteAsync();

        // Act
        bool drained = await body.DrainAsync(CancellationToken.None);

        // Assert
        drained.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: A bare LF in a chunk extension should be rejected as malformed, not kept inside the line")]
    public async Task ReadAsync_OnBareLineFeedInChunkExtension_ShouldRejectAsMalformed()
    {
        // Arrange — kept inside the line, the LF made this a 2-octet chunk "45" and then a last chunk,
        // leaving the smuggled request on the connection. An intermediary that ends the line at the LF
        // reads a 2-octet chunk "xx" and then a 0x45-octet chunk that holds the smuggled request.
        Http1RequestBodyStream body = CreateChunkedBody(Peer("2;\nxx\r\n45\r\n0\r\n\r\n" + SmuggledRequest));

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await body.ReadAsync(new byte[256]));

        // Assert
        failure.ShouldBeOfType<InvalidDataException>();
        body.IsMalformed.ShouldBeTrue();
        (await body.DrainAsync(CancellationToken.None)).ShouldBeFalse();
    }

    /// <summary>
    /// Chunked bodies with a bare CR or a bare LF inside a framing line, each of which a reader that
    /// keeps the octet inside the line accepts.
    /// </summary>
    public static TheoryData<string> BareLineBreaks => new()
    {
        "5;a\rb\r\nhello\r\n0\r\n\r\n",                // a bare CR in a chunk extension
        "5;a=\"b\nc\"\r\nhello\r\n0\r\n\r\n",          // a bare LF in a quoted chunk-extension value
        "5\r\nhello\r\n0\r\nX-A: 1\nX-B: 2\r\n\r\n",  // a bare LF inside a trailer field line
        "5\r\nhello\r\n0\r\nX-A: 1\rX-B: 2\r\n\r\n",  // a bare CR inside a trailer field line
        "5\r\nhello\r\n0\r\nX-A: 1\n\r\n",            // a bare LF that would end the trailer section early
    };

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: A bare CR or LF in a chunk framing line should be rejected as malformed")]
    [MemberData(nameof(BareLineBreaks))]
    public async Task ReadAsync_OnBareLineBreakInFramingLine_ShouldRejectAsMalformed(string wire)
    {
        // Arrange
        Http1RequestBodyStream body = CreateChunkedBody(Peer(wire));

        // Act
        Exception? failure = await Record.ExceptionAsync(() => ReadToEndAsync(body));

        // Assert
        failure.ShouldBeOfType<InvalidDataException>();
        body.IsMalformed.ShouldBeTrue();
    }

    /// <summary>
    /// Chunk-size lines whose chunk extensions break the RFC 9112 §7.1.1 grammar, each of which a reader
    /// that ignores everything after the first ';' accepts.
    /// </summary>
    public static TheoryData<string> MalformedChunkExtensions => new()
    {
        "5;",                    // no extension name
        "5;a=",                  // no extension value
        "5;a=\"open",            // a quoted-string that never closes
        "5;a b",                 // two names with no ';' between them
        "5;a=b c",               // a value that is not one token
        "5;(a)",                 // a name that is not a token
        "5;a\0",                 // NUL
        "5;a\u000B",             // vertical tab
        "5;a=\"b\u0001\"",       // a control character in a quoted-string
        "5;a=\"b\\\u0001\"",     // a control character in a quoted-pair
        "5;a\u007F",             // DEL
        "5;a ",                  // whitespace that ends the line
        "5\u000B;a",             // a vertical tab before the ';'
        "5 ;a",             // a no-break space before the ';'
    };

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: Chunk extensions that break their grammar should be rejected as malformed")]
    [MemberData(nameof(MalformedChunkExtensions))]
    public async Task ReadAsync_OnMalformedChunkExtension_ShouldRejectAsMalformed(string sizeLine)
    {
        // Arrange
        Http1RequestBodyStream body = CreateChunkedBody(Peer(sizeLine + "\r\nhello\r\n0\r\n\r\n"));

        // Act
        Exception? failure = await Record.ExceptionAsync(() => ReadToEndAsync(body));

        // Assert
        failure.ShouldBeOfType<InvalidDataException>();
        body.IsMalformed.ShouldBeTrue();
    }

    /// <summary>
    /// Chunk-size lines whose chunk extensions keep to the RFC 9112 §7.1.1 grammar.
    /// </summary>
    public static TheoryData<string> WellFormedChunkExtensions => new()
    {
        "5;a",
        "5;a=b",
        "5 ;a",
        "5\t; a = b",
        "5;a;b=c;d=\"e\"",
        "5;sig=\"quoted; with spaces, and = signs\"",
        "5;a=\"escaped \\\" quote\"",
        "5;a=\"tab\there\"",
        "5;a=\"obs-text é\"",
        "5;!#$%&'*+-.^_`|~=0",
        "000005;a",
    };

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: Chunk extensions that keep to their grammar should be ignored")]
    [MemberData(nameof(WellFormedChunkExtensions))]
    public async Task ReadAsync_OnWellFormedChunkExtension_ShouldDeliverTheData(string sizeLine)
    {
        // Arrange
        Http1RequestBodyStream body = CreateChunkedBody(Peer(sizeLine + "\r\nhello\r\n0\r\n\r\n"));

        // Act
        byte[] data = await ReadToEndAsync(body);

        // Assert
        Encoding.ASCII.GetString(data).ShouldBe("hello");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Chunked Framing: A request smuggled behind a bare LF in a chunk extension should never be served")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiveAsync_OnBareLineFeedInChunkExtension_ShouldNotServeTheSmuggledRequest(bool readBody)
    {
        // Arrange — read by the application, the body is answered 400; left unread, the drain finds it
        // malformed after the response. Either way the connection closes.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "2;\nxx\r\n45\r\n0\r\n\r\n" + SmuggledRequest);
        TestConnection transport = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport));
        List<string> paths = new();

        // Act
        await using (HttpConnectionListener listener = new(options))
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync();
            IHttpConnectionContext connectionContext = await connection.OpenAsync();

            await foreach (IHttpContext exchange in connectionContext.ReceiveAsync())
            {
                paths.Add(exchange.Request.Path.Value);

                if (readBody)
                {
                    await Record.ExceptionAsync(() => ReadToEndAsync(exchange.Request.Body));
                }

                await connectionContext.SendAsync(exchange);
            }
        }

        string output = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        paths.ShouldBe(["/upload"]);
        output.ShouldStartWith(readBody ? "HTTP/1.1 400" : "HTTP/1.1 200");
        output.Split("HTTP/1.1 ").Length.ShouldBe(2);
    }

    private static CountingInputStream Peer(string wire) => new(Encoding.Latin1.GetBytes(wire), (byte)'a', 0);

    private static async Task<byte[]> ReadToEndAsync(Stream body)
    {
        using MemoryStream data = new();
        byte[] buffer = new byte[256];
        int read;

        while ((read = await body.ReadAsync(buffer)) > 0)
        {
            data.Write(buffer, 0, read);
        }

        return data.ToArray();
    }

    /// <summary>
    /// Sends <paramref name="sentBeforeCancel"/>, reads the body until a read waits for more, and
    /// cancels that read, as an application's own timeout would.
    /// </summary>
    private static async Task<Http1RequestBodyStream> InterruptReadAsync(Pipe peer, string sentBeforeCancel)
    {
        await peer.Writer.WriteAsync(Encoding.ASCII.GetBytes(sentBeforeCancel));
        Http1RequestBodyStream body = CreateChunkedBody(peer.Reader.AsStream());

        using CancellationTokenSource cancellation = new();
        byte[] buffer = new byte[256];
        ValueTask<int> read;

        while ((read = body.ReadAsync(buffer, cancellation.Token)).IsCompleted)
        {
            read.Result.ShouldBePositive("the body ended before the read was cancelled");
        }

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(read.AsTask());
        return body;
    }

    private static Http1RequestBodyStream CreateChunkedBody(Stream peer)
    {
        Http1ConnectionListenerOptions.Http1Limits limits = new()
        {
            MinRequestBodyDataRate = null,
        };

        return new Http1RequestBodyStream(
            peer,
            Http1RequestBodyFraming.Chunked,
            solicitContinue: false,
            interception: null,
            limits,
            TimeProvider.System,
            CancellationToken.None,
            new HttpTrailerCollection(isSupported: true));
    }
}
