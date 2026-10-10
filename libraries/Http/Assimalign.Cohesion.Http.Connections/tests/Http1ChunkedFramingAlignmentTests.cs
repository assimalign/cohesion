using System;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// A chunked HTTP/1.1 request body is only ever decoded at the offsets its peer framed it at. A read
/// that stops inside the framing, cancelled by the application for instance, loses the octets of the
/// line it read so far, so neither a later read nor the keep-alive drain resumes it: resuming would
/// read the rest of the line as a line of its own, and the rest of a chunk-size line <c>40</c> is
/// <c>0</c>, a last chunk, behind which the chunk's data can read as a new request.
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
