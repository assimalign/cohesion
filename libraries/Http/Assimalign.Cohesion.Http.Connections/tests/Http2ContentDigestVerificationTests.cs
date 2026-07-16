using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Proves the Content-Digest verifier's lazy verify-on-read path (#876) against the real HTTP/2
/// frame pump and flow-controlled body pipe: a request whose body exceeds what is buffered at
/// dispatch verifies without stalling the pump (the peer sends the remaining DATA only after the
/// exchange has dispatched), a mid-stream mismatch surfaces as the typed failure on the terminal
/// body read and the exchange aborts with <c>RST_STREAM(CANCEL)</c> through the app-owned abort
/// path, and the HTTP/1.1 eager pre-dispatch <c>400</c> is regression-locked end to end.
/// </summary>
public class Http2ContentDigestVerificationTests
{
    private const int MaxFrame = 16384;
    // The octets on the wire before dispatch; the remainder is written only after the application
    // has observed the dispatched exchange, so it provably was not buffered at dispatch.
    private const int DispatchedChunk = 8192;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ContentDigest: A flow-controlled streamed body verifies without stalling the frame pump")]
    public async Task Http2_OnStreamedBodyWithMatchingDigest_ShouldDispatchVerifyAndComplete()
    {
        // Body: 40,000 deterministic octets; only the first 8,192 are on the wire at dispatch.
        byte[] content = CreateContent(40_000);
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        byte[] opening = Combine(
            Http2TestSettings.Preface(),
            Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>()),
            HeadersFrame(streamId: 1, digest),
            DataFrames(streamId: 1, content.AsSpan(0, DispatchedChunk), endStreamOnLast: false));

        // completeInput: false — the peer's write side stays open; the rest of the body does not
        // exist yet as far as the server can observe.
        TestConnection connection = new(opening, completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpDigestFields.CreateContentDigestVerifier());
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        // The dispatch itself is the deadlock regression guard: the eager verifier read the body
        // to completion inside the parse-path hook, on the very pump that delivers the missing
        // DATA — this MoveNextAsync would never complete. The timeout turns a regression into a
        // clean failure instead of a hung test run.
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        // The application consumes what has arrived so far...
        byte[] first = await ReadExactAsync(context.Request.Body, DispatchedChunk);
        first.ShouldBe(content.AsSpan(0, DispatchedChunk).ToArray());

        // ...and only now does the peer send the remainder — data the parse-path hook could never
        // have read. Consumption-driven WINDOW_UPDATEs (issue #750) keep flowing because the pump
        // was never blocked.
        await connection.WriteInputAsync(DataFrames(streamId: 1, content.AsSpan(DispatchedChunk), endStreamOnLast: true));
        connection.CompleteInput();

        byte[] rest = await ReadExactAsync(context.Request.Body, content.Length - DispatchedChunk);
        rest.ShouldBe(content.AsSpan(DispatchedChunk).ToArray());

        // The terminal read observes end-of-body and resolves the verdict: a match, so a clean EOF.
        (await ReadTerminalAsync(context.Request.Body)).ShouldBe(0);

        // The exchange completes normally.
        context.Response.StatusCode = HttpStatusCode.Ok;
        await httpConnectionContext.SendAsync(context);
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.ReadOutputAsync());
        (long FrameType, byte[] Payload) responseHead = frames.First(f => f.FrameType == 1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(responseHead.Payload)[":status"].ShouldBe("200");
        frames.ShouldNotContain(f => f.FrameType == 3, "a verified exchange must not be reset");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ContentDigest: A streamed-body mismatch surfaces on the terminal read and aborts with RST_STREAM(CANCEL)")]
    public async Task Http2_OnStreamedBodyWithMismatchedDigest_ShouldThrowTypedFailureAndResetStream()
    {
        // The declared digest covers different content than what the peer actually sends.
        byte[] content = CreateContent(20_000);
        byte[] declared = CreateContent(20_000);
        declared[^1] ^= 0xFF;
        string digest = HttpDigestField.ForContent(declared, HttpDigestAlgorithm.Sha256).Serialize();
        byte[] opening = Combine(
            Http2TestSettings.Preface(),
            Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>()),
            HeadersFrame(streamId: 1, digest),
            DataFrames(streamId: 1, content.AsSpan(0, DispatchedChunk), endStreamOnLast: false));

        TestConnection connection = new(opening, completeInput: false);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpDigestFields.CreateContentDigestVerifier());
        options.UseHttp2(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        await ReadExactAsync(context.Request.Body, DispatchedChunk);
        await connection.WriteInputAsync(DataFrames(streamId: 1, content.AsSpan(DispatchedChunk), endStreamOnLast: true));
        connection.CompleteInput();

        // Every content octet still flows to the application — the verdict cannot exist earlier —
        // and the terminal read surfaces the typed failure.
        await ReadExactAsync(context.Request.Body, content.Length - DispatchedChunk);
        HttpContentDigestMismatchException ex = await Should.ThrowAsync<HttpContentDigestMismatchException>(
            () => ReadTerminalAsync(context.Request.Body));
        ex.Algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);

        // Mid-stream rejection semantics: the body was already consumed, so there is no clean 400
        // left — the application aborts the exchange, and the transport resets the single stream.
        context.Cancel();
        await httpConnectionContext.SendAsync(context);
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        AssertFirstResetStream(
            HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.ReadOutputAsync()),
            Http2ErrorCode.Cancel);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 ContentDigest: The eager pre-dispatch 400 rejection is preserved end to end")]
    public async Task Http1_OnMismatchedDigest_ShouldRejectWith400BeforeDispatch()
    {
        // HTTP/1.1 keeps the eager buffer-and-replay: the parse path is its own body reader, so
        // the in-hook full read is free and the mismatch stays a deterministic pre-dispatch 400.
        byte[] body = Encoding.ASCII.GetBytes("the tampered payload");
        string digest = HttpDigestField.ForContent(Encoding.ASCII.GetBytes("the original payload"), HttpDigestAlgorithm.Sha256).Serialize();
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: {body.Length}\r\nContent-Digest: {digest}\r\n\r\n{Encoding.ASCII.GetString(body)}");

        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpDigestFields.CreateContentDigestVerifier());
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        bool yielded;
        await using (IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator())
        {
            yielded = await enumerator.MoveNextAsync();
        }

        // No exchange is dispatched; the transport answers the rejection itself.
        yielded.ShouldBeFalse();
        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        response.ShouldStartWith("HTTP/1.1 400");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 ContentDigest: A matching digest replays the body to the application end to end")]
    public async Task Http1_OnMatchingDigest_ShouldReplayBodyAndComplete()
    {
        byte[] body = Encoding.ASCII.GetBytes("payload that matches its digest");
        string digest = HttpDigestField.ForContent(body, HttpDigestAlgorithm.Sha256).Serialize();
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: {body.Length}\r\nContent-Digest: {digest}\r\n\r\n{Encoding.ASCII.GetString(body)}");

        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpDigestFields.CreateContentDigestVerifier());
        options.UseHttp1(new TestConnectionListener(connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext httpConnectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = httpConnectionContext.ReceiveAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        IHttpContext context = enumerator.Current;

        // The hook consumed the wire body to hash it; the application observes the replay.
        byte[] observed = await ReadExactAsync(context.Request.Body, body.Length);
        observed.ShouldBe(body);

        context.Response.StatusCode = HttpStatusCode.Ok;
        await httpConnectionContext.SendAsync(context);
        (await enumerator.MoveNextAsync()).ShouldBeFalse();

        string response = Encoding.ASCII.GetString(await connection.ReadOutputAsync());
        response.ShouldStartWith("HTTP/1.1 200");
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] CreateContent(int length)
    {
        byte[] content = new byte[length];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }
        return content;
    }

    private static byte[] HeadersFrame(int streamId, string contentDigest)
        => HttpProtocolPayloadFactory.CreateHttp2HeadersFrame(
            streamId,
            0x4, // END_HEADERS only — the body follows in DATA frames
            (":method", "POST"),
            (":scheme", "https"),
            (":path", "/upload"),
            (":authority", "api.test"),
            ("content-digest", contentDigest));

    /// <summary>
    /// Builds one or more DATA frames on <paramref name="streamId"/> carrying
    /// <paramref name="content"/>, each frame no larger than the advertised max frame size, with
    /// END_STREAM set on the final frame when <paramref name="endStreamOnLast"/> is
    /// <see langword="true"/>.
    /// </summary>
    private static byte[] DataFrames(int streamId, ReadOnlySpan<byte> content, bool endStreamOnLast)
    {
        using MemoryStream stream = new();
        int offset = 0;

        do
        {
            int chunk = Math.Min(MaxFrame, content.Length - offset);
            bool last = offset + chunk == content.Length;
            byte flags = last && endStreamOnLast ? (byte)0x1 : (byte)0x0;
            byte[] frame = Http2TestSettings.RawFrame(0x0, flags, streamId, content.Slice(offset, chunk).ToArray());
            stream.Write(frame, 0, frame.Length);
            offset += chunk;
        }
        while (offset < content.Length);

        return stream.ToArray();
    }

    /// <summary>
    /// Asserts the first <c>RST_STREAM</c> on the wire carries <paramref name="expectedErrorCode"/>.
    /// Only the first is asserted because the reset removes the stream, so any DATA already in
    /// flight afterward legitimately draws a follow-up <c>RST_STREAM(STREAM_CLOSED)</c>
    /// (RFC 9113 §5.1).
    /// </summary>
    private static void AssertFirstResetStream(
        IReadOnlyList<(long FrameType, byte[] Payload)> frames,
        Http2ErrorCode expectedErrorCode)
    {
        foreach ((long frameType, byte[] framePayload) in frames)
        {
            if (frameType == 3) // RST_STREAM
            {
                framePayload.Length.ShouldBe(4);
                uint code = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(framePayload);
                code.ShouldBe((uint)expectedErrorCode);
                return;
            }
        }

        throw new ShouldAssertException("Expected an RST_STREAM frame on the wire, but none was found.");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;

        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            read.ShouldBeGreaterThan(0, $"the body stream ended after {offset} of {count} expected octets");
            offset += read;
        }

        return buffer;
    }

    /// <summary>
    /// Issues one read against a body expected to be at end-of-body, returning the transferred
    /// count — the read on which the lazy verifier resolves its verdict.
    /// </summary>
    private static Task<int> ReadTerminalAsync(Stream stream) => stream.ReadAsync(new byte[8], 0, 8);

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }
}
