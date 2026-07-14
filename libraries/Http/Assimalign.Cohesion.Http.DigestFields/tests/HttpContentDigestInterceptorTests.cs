using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpContentDigestInterceptorTests
{
    // ------------------------------------------------------------ eager path (HTTP/1.1, HTTP/3)

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A matching Content-Digest passes and replays the body (eager protocols)")]
    [InlineData(HttpVersion.Http11)]
    [InlineData(HttpVersion.Http30)]
    public void AfterRequestBody_EagerMatch_ReplaysBody(HttpVersion version)
    {
        byte[] content = Encoding.UTF8.GetBytes("payload that matches its digest");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, version);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();
        using var body = new MemoryStream(content);

        Stream result = verifier.AfterRequestBody(context, body);

        // The eager path hashes in-hook and hands the application a replay of the buffered body:
        // the original was consumed to end inside the hook, and the replay is independently
        // seekable (the lazy wrapper is not).
        result.ShouldNotBeSameAs(body);
        body.Position.ShouldBe(body.Length);
        result.CanSeek.ShouldBeTrue();
        using var read = new MemoryStream();
        result.CopyTo(read);
        read.ToArray().ShouldBe(content);
    }

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A mismatched Content-Digest is rejected with 400 before dispatch (eager protocols)")]
    [InlineData(HttpVersion.Http11)]
    [InlineData(HttpVersion.Http30)]
    public void AfterRequestBody_EagerMismatch_Rejects400(HttpVersion version)
    {
        byte[] declared = Encoding.UTF8.GetBytes("the original payload");
        byte[] actual = Encoding.UTF8.GetBytes("the tampered payload");
        string digest = HttpDigestField.ForContent(declared, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, version);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();

        HttpRequestRejectedException ex = Should.Throw<HttpRequestRejectedException>(
            () => verifier.AfterRequestBody(context, new MemoryStream(actual)));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: No Content-Digest passes through unchanged")]
    public void AfterRequestBody_NoDigest_PassesThrough()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(contentDigest: null);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        Stream result = verifier.AfterRequestBody(context, body);

        result.ShouldBeSameAs(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A deprecated-only Content-Digest passes through unverified")]
    public void AfterRequestBody_DeprecatedOnly_PassesThrough()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext("md5=:1B2M2Y8AsgTpgAmY7PhCfg==:");
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        Stream result = verifier.AfterRequestBody(context, body);

        result.ShouldBeSameAs(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: Disposing the replay stream disposes the original body")]
    public void AfterRequestBody_ReplayDisposal_DisposesOriginal()
    {
        byte[] content = Encoding.UTF8.GetBytes("owned body");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest);
        var original = new TrackingStream(content);

        Stream result = HttpDigestFields.CreateContentDigestVerifier().AfterRequestBody(context, original);
        result.Dispose();

        original.IsDisposed.ShouldBeTrue();
    }

    // ------------------------------------------------------------ lazy path (HTTP/2)

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: The HTTP/2 hook is CPU-only — it wraps without reading a single body octet")]
    public void AfterRequestBody_OnHttp2_DoesNotReadBodyInHook()
    {
        // On h2 the hook runs on the connection's frame pump while the body may still be arriving;
        // reading it in-hook is the deadlock this rework removes. A body that throws on any read
        // proves the hook never touches it.
        byte[] content = Encoding.UTF8.GetBytes("still arriving");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();

        // The eager path would throw from ReadForbiddenStream here; returning at all proves the
        // hook stayed CPU-only.
        Stream result = verifier.AfterRequestBody(context, new ReadForbiddenStream());

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<ReadForbiddenStream>();
        result.CanSeek.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 body matching its digest streams through and verifies at end-of-body")]
    public async Task AfterRequestBody_OnHttp2Match_StreamsAndVerifiesAtEof()
    {
        byte[] content = Encoding.UTF8.GetBytes("a streamed h2 body verified as the application reads");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(content, maxChunk: 7));

        byte[] observed = await ReadToEndAsync(wrapped);

        observed.ShouldBe(content);
        // End-of-body was verified; the stream stays at a clean EOF afterward.
        (await wrapped.ReadAsync(new byte[8])).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 digest mismatch surfaces as the typed failure on the terminal read")]
    public async Task AfterRequestBody_OnHttp2Mismatch_ThrowsTypedFailureOnTerminalRead()
    {
        byte[] declared = Encoding.UTF8.GetBytes("the original payload");
        byte[] actual = Encoding.UTF8.GetBytes("the tampered payload");
        string digest = HttpDigestField.ForContent(declared, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(actual, maxChunk: 5));

        // Every content octet flows to the application unimpeded — the verdict cannot exist until
        // end-of-body, so the reads that carry data must succeed.
        byte[] observed = await ReadExactAsync(wrapped, actual.Length);
        observed.ShouldBe(actual);

        HttpContentDigestMismatchException ex = await Should.ThrowAsync<HttpContentDigestMismatchException>(
            () => ReadTerminalAsync(wrapped));
        ex.Algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
        ex.Code.ShouldBe(HttpErrorCode.ReadingError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 multi-digest field verifies every supported algorithm")]
    public async Task AfterRequestBody_OnHttp2MultiDigestMatch_VerifiesAllAlgorithms()
    {
        byte[] content = Encoding.UTF8.GetBytes("content hashed with two algorithms");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256, HttpDigestAlgorithm.Sha512).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(content, maxChunk: 11));

        byte[] observed = await ReadToEndAsync(wrapped);

        observed.ShouldBe(content);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 multi-digest field fails when any one algorithm mismatches, naming it")]
    public async Task AfterRequestBody_OnHttp2MultiDigestOneWrong_ThrowsNamingTheAlgorithm()
    {
        byte[] content = Encoding.UTF8.GetBytes("content whose sha-512 digest is wrong");
        // sha-256 is computed over the real content; sha-512 over different content.
        HttpDigestField sha256 = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256);
        HttpDigestField sha512 = HttpDigestField.ForContent(Encoding.UTF8.GetBytes("other"), HttpDigestAlgorithm.Sha512);
        string digest = $"{sha256.Serialize()}, {sha512.Serialize()}";
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(content, maxChunk: 9));

        await ReadExactAsync(wrapped, content.Length);

        HttpContentDigestMismatchException ex = await Should.ThrowAsync<HttpContentDigestMismatchException>(
            () => ReadTerminalAsync(wrapped));
        ex.Algorithm.ShouldBe(HttpDigestAlgorithm.Sha512);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 mismatch is sticky — every later read rethrows")]
    public async Task AfterRequestBody_OnHttp2MismatchThenReadAgain_RethrowsSticky()
    {
        byte[] actual = Encoding.UTF8.GetBytes("tampered");
        string digest = HttpDigestField.ForContent(Encoding.UTF8.GetBytes("original"), HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(actual, maxChunk: 3));
        await ReadExactAsync(wrapped, actual.Length);
        await Should.ThrowAsync<HttpContentDigestMismatchException>(() => ReadTerminalAsync(wrapped));

        // A consumer that swallows the first failure must not be able to observe a healthy stream.
        await Should.ThrowAsync<HttpContentDigestMismatchException>(() => ReadTerminalAsync(wrapped));
        Should.Throw<HttpContentDigestMismatchException>(() => wrapped.Read(new byte[8], 0, 8));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 malformed Content-Digest is still rejected 400 in-hook without touching the body")]
    public void AfterRequestBody_OnHttp2Malformed_StillRejects400InHook()
    {
        // Parsing is CPU-only, so the fail-closed pre-dispatch rejection survives on h2 — only the
        // body-dependent verdict moves to the application's reads.
        HttpExchangeInterceptorRequestContext context = CreateContext("sha-256=12345", HttpVersion.Http20);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();

        HttpRequestRejectedException ex = Should.Throw<HttpRequestRejectedException>(
            () => verifier.AfterRequestBody(context, new ReadForbiddenStream()));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An HTTP/2 request with nothing verifiable passes the body through untouched")]
    [InlineData(null)]
    [InlineData("md5=:1B2M2Y8AsgTpgAmY7PhCfg==:")]
    public void AfterRequestBody_OnHttp2NothingVerifiable_PassesThroughSameStream(string? contentDigest)
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(contentDigest, HttpVersion.Http20);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        Stream result = verifier.AfterRequestBody(context, body);

        result.ShouldBeSameAs(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An empty HTTP/2 body verifies against the digest of empty content")]
    public async Task AfterRequestBody_OnHttp2EmptyBodyMatch_Verifies()
    {
        string digest = HttpDigestField.ForContent(ReadOnlySpan<byte>.Empty, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(Array.Empty<byte>(), maxChunk: 4));

        (await wrapped.ReadAsync(new byte[8])).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: An empty HTTP/2 body fails a digest declared over non-empty content")]
    public async Task AfterRequestBody_OnHttp2EmptyBodyMismatch_Throws()
    {
        string digest = HttpDigestField.ForContent(Encoding.UTF8.GetBytes("expected content"), HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(Array.Empty<byte>(), maxChunk: 4));

        await Should.ThrowAsync<HttpContentDigestMismatchException>(() => ReadTerminalAsync(wrapped));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A zero-length read is not mistaken for end-of-body")]
    public async Task AfterRequestBody_OnHttp2ZeroLengthRead_DoesNotResolveVerdict()
    {
        byte[] content = Encoding.UTF8.GetBytes("body");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(content, maxChunk: 64));

        // A zero-length read returns 0 by definition; the verdict must wait for real end-of-body.
        (await wrapped.ReadAsync(Memory<byte>.Empty)).ShouldBe(0);

        byte[] observed = await ReadToEndAsync(wrapped);
        observed.ShouldBe(content);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: The HTTP/2 lazy path verifies over synchronous reads too")]
    public void AfterRequestBody_OnHttp2SyncReads_VerifyAtEof()
    {
        byte[] declared = Encoding.UTF8.GetBytes("the original payload");
        byte[] actual = Encoding.UTF8.GetBytes("the tampered payload");
        string digest = HttpDigestField.ForContent(declared, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier()
            .AfterRequestBody(context, new ChunkedReadStream(actual, maxChunk: 6));

        byte[] buffer = new byte[actual.Length];
        int offset = 0;
        while (offset < buffer.Length)
        {
            offset += wrapped.Read(buffer, offset, buffer.Length - offset);
        }

        buffer.ShouldBe(actual);
        Should.Throw<HttpContentDigestMismatchException>(() => wrapped.Read(new byte[8], 0, 8));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: Disposing the HTTP/2 wrapper disposes the wrapped body, verified or not")]
    public async Task AfterRequestBody_OnHttp2WrapperDisposal_DisposesWrappedBody()
    {
        byte[] content = Encoding.UTF8.GetBytes("owned body");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();

        // Disposed mid-body — no verdict was ever resolved.
        HttpExchangeInterceptorRequestContext context = CreateContext(digest, HttpVersion.Http20);
        var unread = new TrackingStream(content);
        Stream wrapped = HttpDigestFields.CreateContentDigestVerifier().AfterRequestBody(context, unread);
        wrapped.Dispose();
        unread.IsDisposed.ShouldBeTrue();

        // Disposed after a verified end-of-body.
        context = CreateContext(digest, HttpVersion.Http20);
        var drained = new TrackingStream(content);
        wrapped = HttpDigestFields.CreateContentDigestVerifier().AfterRequestBody(context, drained);
        await ReadToEndAsync(wrapped);
        wrapped.Dispose();
        drained.IsDisposed.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ helpers

    private static HttpExchangeInterceptorRequestContext CreateContext(
        string? contentDigest,
        HttpVersion version = HttpVersion.Http11)
    {
        var headers = new HttpHeaderCollection();
        if (contentDigest is not null)
        {
            headers.Add(HttpHeaderKey.ContentDigest, contentDigest);
        }

        return new HttpExchangeInterceptorRequestContext
        {
            Version = version,
            Method = HttpMethod.Post,
            Path = new HttpPath("/upload"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = headers.AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };
    }

    /// <summary>
    /// Issues one read against a stream expected to be at end-of-body, returning the transferred
    /// count — the read on which the lazy verifier resolves its verdict.
    /// </summary>
    private static Task<int> ReadTerminalAsync(Stream stream) => stream.ReadAsync(new byte[8], 0, 8);

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        byte[] buffer = new byte[16];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            read.ShouldBeGreaterThan(0, $"the stream ended after {offset} of {count} expected octets");
            offset += read;
        }
        return buffer;
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] content) : base(content, writable: false)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A body double proving the hook never reads: any read attempt fails the test. This is the
    /// package-level stand-in for the h2 frame pump, where an in-hook read deadlocks.
    /// </summary>
    private sealed class ReadForbiddenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new InvalidOperationException("The parse-path hook must not read the request body.");
        public override int Read(Span<byte> buffer)
            => throw new InvalidOperationException("The parse-path hook must not read the request body.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The parse-path hook must not read the request body.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The parse-path hook must not read the request body.");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A forward-only stream that serves at most <c>maxChunk</c> octets per read, so tests prove
    /// the wrapper hashes incrementally across many partial reads — the shape of a
    /// flow-controlled h2 body.
    /// </summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] _content;
        private readonly int _maxChunk;
        private int _position;

        public ChunkedReadStream(byte[] content, int maxChunk)
        {
            _content = content;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            int take = Math.Min(Math.Min(buffer.Length, _maxChunk), _content.Length - _position);
            _content.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
