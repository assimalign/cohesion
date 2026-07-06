using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies HTTP/2 request-body flow-control backpressure (issue #750 /
/// RFC 9113 §5.2): WINDOW_UPDATE is driven by application consumption rather than
/// receipt, per-stream buffering is bounded by the advertised receive window, and
/// a sender that exceeds the window is failed with FLOW_CONTROL_ERROR.
/// </summary>
public class Http2FlowControlBackpressureTests
{
    // The server advertises SETTINGS_INITIAL_WINDOW_SIZE = 65535 and the
    // connection receive window is fixed at 65535 (RFC 9113 §6.5.2).
    private const int Window = 65535;
    private const int MaxFrame = 16384;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should not emit WINDOW_UPDATE for received-but-unconsumed body")]
    public async Task Http2_OnUnconsumedBody_ShouldNotEmitWindowUpdate()
    {
        // RFC 9113 §5.2 — flow control is consumption-driven. DATA that arrives but
        // is never read by the application must NOT be credited back to the peer,
        // so buffering stays bounded and a slow reader applies backpressure.
        byte[] request = BuildBodyRequest(streamId: 1, bodyLength: 30_000, endStream: false);
        TestConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Drain the receive enumerable without reading any request body. The pump
        // buffers the 30 KB body but the application never consumes it.
        await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
        {
        }

        byte[] output = await connection.ReadOutputAsync();
        ParseWindowUpdates(output).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should FLOW_CONTROL_ERROR when the body exceeds the advertised window")]
    public async Task Http2_OnBodyExceedingWindow_ShouldGoAwayFlowControlError()
    {
        // RFC 9113 §6.9.1 — a peer that sends more DATA than the advertised receive
        // window without waiting for a WINDOW_UPDATE is a FLOW_CONTROL_ERROR. This
        // is the guard that bounds per-stream buffering: exactly the window is
        // acceptable, one octet more is fatal. (Existing enforcement, regression-tested.)
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HeadersFrame(streamId: 1, endStream: false);
        // Exactly the window, then one more octet — the extra byte overflows the
        // connection-level receive window and the pump rejects it.
        byte[] body = DataFrames(streamId: 1, totalLength: Window, endStreamOnLast: false);
        byte[] overflow = Http2TestSettings.RawFrame(0x0, 0, 1, new byte[1]);

        byte[] payload = Combine(preface, settings, headers, body, overflow);
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Drive the receive enumerable to completion without reading the body, so
        // the pump consumes the whole window and then trips on the overflow octet.
        await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
        {
        }

        byte[] output = await connection.ReadOutputAsync();
        Http2TestSettings.AssertContainsGoAway(output, Http2ErrorCode.FlowControlError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should credit WINDOW_UPDATE as the application consumes the body")]
    public async Task Http2_OnBodyConsumption_ShouldCreditStreamAndConnectionWindows()
    {
        // RFC 9113 §5.2 / §6.9 — as the application drains the body, the consumed
        // octets are credited back to BOTH the stream and connection receive
        // windows via WINDOW_UPDATE, which is what lets a stalled sender resume.
        const int bodyLength = 30_000;
        byte[] request = BuildBodyRequest(streamId: 1, bodyLength: bodyLength, endStream: true);
        TestConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext context = await ReadFirstContextAsync(httpConnectionContext);

        byte[] body = await ReadToEndAsync(context.Request.Body);
        body.Length.ShouldBe(bodyLength);

        byte[] output = await connection.ReadOutputAsync();
        List<(int StreamId, int Increment)> windowUpdates = ParseWindowUpdates(output);

        // Every consumed octet is credited exactly once at each level.
        windowUpdates.Where(w => w.StreamId == 1).Sum(w => w.Increment).ShouldBe(bodyLength);
        windowUpdates.Where(w => w.StreamId == 0).Sum(w => w.Increment).ShouldBe(bodyLength);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should stall a full-window sender then resume it when the reader drains")]
    public async Task Http2_OnSlowReader_ShouldStallSenderThenResumeOnDrain()
    {
        // End-to-end backpressure over the raw-frame harness (RFC 9113 §5.2):
        //  1. The peer fills the entire advertised window without the app reading —
        //     the window drops to zero and NO credit is returned (sender stalled).
        //  2. The app drains the buffered body — the full window is credited back
        //     via WINDOW_UPDATE (sender resumes).
        //  3. The peer, now re-credited, sends the remainder, which is delivered.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HeadersFrame(streamId: 1, endStream: false);
        byte[] firstWindow = DataFrames(streamId: 1, totalLength: Window, endStreamOnLast: false);

        // completeInput: false keeps the peer's write side open so we can model a
        // sender that only continues after it is credited more window.
        TestConnection connection = new(Combine(preface, settings, headers, firstWindow), completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection httpConnection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext httpConnectionContext = await httpConnection.OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        // Before the application reads anything, the peer has filled the window and
        // received no credit — the only output so far is SETTINGS (+ its ACK).
        byte[] beforeRead = await connection.ReadOutputAsync();
        ParseWindowUpdates(beforeRead).ShouldBeEmpty();

        // Drain the full window worth of body — this credits it back.
        byte[] firstChunk = await ReadExactAsync(context.Request.Body, Window);
        firstChunk.Length.ShouldBe(Window);

        byte[] afterRead = await connection.ReadOutputAsync();
        List<(int StreamId, int Increment)> credits = ParseWindowUpdates(afterRead);
        credits.Where(w => w.StreamId == 1).Sum(w => w.Increment).ShouldBe(Window);
        credits.Where(w => w.StreamId == 0).Sum(w => w.Increment).ShouldBe(Window);

        // The resumed sender delivers the remainder within the re-credited window.
        const int remainder = 4096;
        await connection.WriteInputAsync(DataFrames(streamId: 1, totalLength: remainder, endStreamOnLast: true));
        connection.CompleteInput();

        byte[] rest = await ReadToEndAsync(context.Request.Body);
        rest.Length.ShouldBe(remainder);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should stream a complete small body to the application in order")]
    public async Task Http2_OnCompleteBody_ShouldDeliverBodyContentInOrder()
    {
        // Happy path: a complete request body (HEADERS + DATA + END_STREAM) streams
        // through the flow-control-aware pipe and arrives byte-for-byte in order.
        byte[] bodyBytes = new byte[5000];
        for (int i = 0; i < bodyBytes.Length; i++)
        {
            bodyBytes[i] = (byte)(i % 251);
        }

        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HeadersFrame(streamId: 1, endStream: false);
        byte[] data = Http2TestSettings.RawFrame(0x0, 0x1 /* END_STREAM */, 1, bodyBytes);

        TestConnection connection = new(Combine(preface, settings, headers, data));
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        IHttpContext context = await ReadFirstContextAsync(httpConnectionContext);

        byte[] received = await ReadToEndAsync(context.Request.Body);
        received.ShouldBe(bodyBytes);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should reclaim the connection window when a handler ignores the body")]
    public async Task Http2_OnUnreadBodyThenRemoval_ShouldCreditConnectionWindow()
    {
        // Because credit is consumption-driven, a handler that responds without
        // reading the request body would otherwise leave those octets debited
        // against the shared connection window forever. On stream removal the debt
        // must be credited back to the peer (a connection-level WINDOW_UPDATE), or
        // the connection window would shrink permanently across many such requests.
        const int bodyLength = 20_000;
        byte[] request = BuildBodyRequest(streamId: 1, bodyLength: bodyLength, endStream: true);
        TestConnection connection = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext exchange = enumerator.Current;

        // Respond without reading a single body byte, then drain the pump so all
        // reclaim/discard WINDOW_UPDATEs are flushed before we inspect the wire.
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        await httpConnectionContext.SendAsync(exchange);
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        byte[] output = await connection.ReadOutputAsync();
        List<(int StreamId, int Increment)> windowUpdates = ParseWindowUpdates(output);

        // The whole ignored body is credited back at the connection level.
        windowUpdates.Where(w => w.StreamId == 0).Sum(w => w.Increment).ShouldBe(bodyLength);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should GOAWAY when a padded DATA frame's padding meets or exceeds the payload")]
    public async Task Http2_OnPaddedDataOverflow_ShouldGoAwayProtocolError()
    {
        // RFC 9113 §6.1 — if the padding length equals the frame payload length or
        // is greater, it is a connection error of type PROTOCOL_ERROR.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HeadersFrame(streamId: 1, endStream: false);
        // PADDED (0x8) DATA frame: pad-length octet declares 5 padding octets, but
        // only 2 octets follow it — the padding overruns the frame payload.
        byte[] paddedData = Http2TestSettings.RawFrame(0x0, 0x8, 1, new byte[] { 5, 0, 0 });

        await AssertGoAwayAsync(Combine(preface, settings, headers, paddedData), Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 FlowControl: Should abort a body read when the connection tears down mid-body")]
    public async Task Http2_OnTruncatedBody_ShouldAbortReaderNotReturnCleanEof()
    {
        // A body still incoming when the connection tears down (here: the peer's
        // input ends before END_STREAM) MUST surface to the handler as an abort,
        // not a clean end-of-stream — otherwise a handler mistakes a truncated
        // upload for a complete one.
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HeadersFrame(streamId: 1, endStream: false);
        byte[] partialBody = DataFrames(streamId: 1, totalLength: 1000, endStreamOnLast: false);

        // completeInput: true ends the peer's stream after the partial body, with
        // no END_STREAM — the pump reaches end-of-input mid-body and tears down.
        TestConnection connection = new(Combine(preface, settings, headers, partialBody));
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        // Drain to completion: the pump exits at end-of-input and fires the abort
        // before completing the ready-context channel.
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            byte[] buffer = new byte[2000];
            await context.Request.Body.ReadAsync(buffer);
        });
    }

    private static byte[] BuildBodyRequest(int streamId, int bodyLength, bool endStream)
    {
        byte[] preface = Http2TestSettings.Preface();
        byte[] settings = Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>());
        byte[] headers = HeadersFrame(streamId, endStream: false);
        byte[] body = DataFrames(streamId, bodyLength, endStreamOnLast: endStream);
        return Combine(preface, settings, headers, body);
    }

    private static byte[] HeadersFrame(int streamId, bool endStream)
    {
        // 0x4 = END_HEADERS; add 0x1 (END_STREAM) only for a body-less request.
        byte flags = endStream ? (byte)(0x4 | 0x1) : (byte)0x4;
        return HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId,
            flags,
            (":method", "POST"),
            (":scheme", "https"),
            (":path", "/upload"),
            (":authority", "api.test"));
    }

    /// <summary>
    /// Builds one or more DATA frames on <paramref name="streamId"/> carrying
    /// <paramref name="totalLength"/> zero octets, each frame no larger than the
    /// advertised max frame size, with END_STREAM set on the final frame when
    /// <paramref name="endStreamOnLast"/> is <see langword="true"/>.
    /// </summary>
    private static byte[] DataFrames(int streamId, int totalLength, bool endStreamOnLast)
    {
        using MemoryStream stream = new();
        int remaining = totalLength;

        do
        {
            int chunk = Math.Min(MaxFrame, remaining);
            remaining -= chunk;
            bool last = remaining == 0;
            byte flags = last && endStreamOnLast ? (byte)0x1 : (byte)0x0;
            byte[] frame = Http2TestSettings.RawFrame(0x0, flags, streamId, new byte[chunk]);
            stream.Write(frame, 0, frame.Length);
        }
        while (remaining > 0);

        return stream.ToArray();
    }

    /// <summary>
    /// Extracts every WINDOW_UPDATE frame (type 0x8) from a captured output stream
    /// as (stream id, increment) pairs.
    /// </summary>
    private static List<(int StreamId, int Increment)> ParseWindowUpdates(byte[] output)
    {
        List<(int StreamId, int Increment)> result = new();
        int index = 0;

        while (index + 9 <= output.Length)
        {
            int length = (output[index] << 16) | (output[index + 1] << 8) | output[index + 2];
            byte type = output[index + 3];
            int streamId = ((output[index + 5] & 0x7F) << 24)
                | (output[index + 6] << 16)
                | (output[index + 7] << 8)
                | output[index + 8];
            int payloadStart = index + 9;

            if (type == 0x8 && length == 4)
            {
                int increment = ((output[payloadStart] & 0x7F) << 24)
                    | (output[payloadStart + 1] << 16)
                    | (output[payloadStart + 2] << 8)
                    | output[payloadStart + 3];
                result.Add((streamId, increment));
            }

            index = payloadStart + length;
        }

        return result;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;

        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                throw new EndOfStreamException($"The body stream ended after {offset} of {count} expected octets.");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using MemoryStream copy = new();
        byte[] buffer = new byte[8192];

        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return copy.ToArray();
            }

            copy.Write(buffer, 0, read);
        }
    }

    private static async Task<IHttpContext> ReadFirstContextAsync(IHttpConnectionContext context)
    {
        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }

    private static async Task AssertGoAwayAsync(byte[] payload, Http2ErrorCode expectedErrorCode)
    {
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // The pump must absorb the protocol violation: emit GOAWAY on the wire and
        // complete the enumerable without propagating, so a malformed peer cannot
        // crash the listener.
        await foreach (IHttpContext _ in httpConnectionContext.ReceiveAsync())
        {
        }

        byte[] output = await connection.ReadOutputAsync();
        Http2TestSettings.AssertContainsGoAway(output, expectedErrorCode);
    }
}
