using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A read-only wrapper that verifies a request body against its <c>Content-Digest</c> lazily, as
/// the application reads it: every octet read is fed to one BCL <see cref="IncrementalHash"/> per
/// supported digest entry, and the computed digests are compared on the <em>terminal</em> read —
/// the read that observes end-of-body. A mismatch throws
/// <see cref="HttpContentDigestMismatchException"/> from that read (and, stickily, from every read
/// after it); a match lets the end-of-body surface as a normal zero-length read.
/// </summary>
/// <remarks>
/// <para>
/// This is the verification shape for transports whose request body may still be arriving when
/// <c>AfterRequestBody</c> runs (HTTP/2, where the hook executes on the connection's single frame
/// pump and an in-hook read of the flow-controlled body would stall the very pump that feeds it).
/// Construction is CPU-only — no octet is read here — so the hook honors its contract; the wire
/// wait moves to the application's own reads, where waiting is safe and where flow-control
/// backpressure keeps buffering bounded. The cost is that the verdict arrives only at end-of-body:
/// content is necessarily observed by the application before it is proven authentic, and a body
/// the application never drains is never verified.
/// </para>
/// <para>
/// The wrapper owns the stream it wraps (the seam's disposal contract): disposing it disposes the
/// wrapped body and the running hashes. Reads are single-consumer, like the transport body streams
/// it wraps.
/// </para>
/// </remarks>
internal sealed class HttpDigestVerifyingStream : Stream
{
    private readonly Stream _inner;
    // The supported digest entries under verification and one running hash per entry, index-paired.
    private readonly HttpDigestEntry[] _entries;
    private readonly IncrementalHash[] _hashes;

    private bool _verified;
    private HttpDigestAlgorithm? _failedAlgorithm;
    private bool _disposed;

    public HttpDigestVerifyingStream(HttpDigestField field, Stream inner)
    {
        _inner = inner;

        int supported = 0;
        foreach (HttpDigestEntry entry in field.Entries)
        {
            if (entry.Algorithm.IsSupported)
            {
                supported++;
            }
        }

        _entries = new HttpDigestEntry[supported];
        _hashes = new IncrementalHash[supported];

        int index = 0;
        foreach (HttpDigestEntry entry in field.Entries)
        {
            if (entry.Algorithm.IsSupported)
            {
                _entries[index] = entry;
                _hashes[index] = entry.Algorithm.CreateIncrementalHash();
                index++;
            }
        }

        // Nothing verifiable (the interceptor never constructs the wrapper this way): the empty
        // verdict is a pass, so reads degrade to pure pass-through.
        _verified = supported == 0;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("The digest-verified request body length is not known in advance.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("The digest-verified request body stream is not seekable.");
        set => throw new NotSupportedException("The digest-verified request body stream is not seekable.");
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // A zero-length read returns zero by definition; it must not be observed as end-of-body.
        if (buffer.IsEmpty)
        {
            return 0;
        }

        ThrowIfFailed();

        if (_verified)
        {
            // End-of-body was already observed and verified; the stream stays at EOF.
            return 0;
        }

        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span, read);
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        ThrowIfFailed();

        if (_verified)
        {
            return 0;
        }

        int read = _inner.Read(buffer);
        Observe(buffer, read);
        return read;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // A request body is read-only; there is nothing to flush.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The digest-verified request body stream is not seekable.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("The digest-verified request body stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The digest-verified request body stream is read-only.");

    /// <summary>
    /// Feeds octets that flowed to the application into the running hashes, or — on the terminal
    /// read — resolves the verdict.
    /// </summary>
    /// <param name="buffer">The read destination.</param>
    /// <param name="read">The octet count the wrapped stream returned.</param>
    /// <exception cref="HttpContentDigestMismatchException">The content failed a declared digest.</exception>
    private void Observe(ReadOnlySpan<byte> buffer, int read)
    {
        if (read > 0)
        {
            foreach (IncrementalHash hash in _hashes)
            {
                hash.AppendData(buffer[..read]);
            }
            return;
        }

        // End-of-body: every content octet has been hashed, so the verdict exists now. Resolve it
        // exactly once; the hashes are no longer needed either way.
        Span<byte> computed = stackalloc byte[64]; // large enough for SHA-512
        for (int i = 0; i < _entries.Length; i++)
        {
            int written = _hashes[i].GetHashAndReset(computed);

            // FixedTimeEquals is length-checked, so a truncated or oversized digest value is a
            // mismatch rather than an exception — same rule as the eager verifier.
            if (!CryptographicOperations.FixedTimeEquals(computed[..written], _entries[i].Digest.Span))
            {
                _failedAlgorithm = _entries[i].Algorithm;
                DisposeHashes();
                ThrowIfFailed();
            }
        }

        _verified = true;
        DisposeHashes();
    }

    private void ThrowIfFailed()
    {
        if (_failedAlgorithm is { } algorithm)
        {
            // Sticky: the content is proven corrupt/tampered, so no later read may present the
            // stream as healthy. Each throw is a fresh instance to keep stack traces honest.
            throw new HttpContentDigestMismatchException(algorithm);
        }
    }

    private void DisposeHashes()
    {
        foreach (IncrementalHash hash in _hashes)
        {
            hash.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_verified && _failedAlgorithm is null)
            {
                // Disposed before end-of-body was observed — no verdict was ever resolved, so the
                // hashes are still live.
                DisposeHashes();
            }
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
