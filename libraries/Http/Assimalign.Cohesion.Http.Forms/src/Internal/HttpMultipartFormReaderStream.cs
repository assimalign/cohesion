using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Read-only stream wrapper around a <see cref="BufferedReadStream"/> that
/// yields bytes belonging to a single multipart section. Stops at the next
/// boundary (or final boundary) on the wire without consuming the boundary
/// octets &#8211; the parent <c>MultipartReader</c> consumes them when
/// advancing to the next section.
/// </summary>
/// <remarks>
/// Ported from ASP.NET Core's
/// <c>Microsoft.AspNetCore.WebUtilities.HttpMultipartFormReaderStream</c>. Uses
/// the boundary's Boyer-Moore-Horspool skip table for fast boundary scans
/// over arbitrary section bodies.
/// </remarks>
internal sealed class HttpMultipartFormReaderStream : Stream
{
    private readonly BufferedReadStream _inner;
    private readonly HttpMultipartFormBoundary _boundary;
    private long _position;
    private long _observedLength;
    private bool _finished;

    public HttpMultipartFormReaderStream(BufferedReadStream inner, HttpMultipartFormBoundary boundary)
    {
        _inner = inner;
        _boundary = boundary;
    }

    /// <summary>
    /// Optional cap on the number of body bytes a single section may yield.
    /// Reads past the limit throw <see cref="InvalidDataException"/>. Null
    /// disables the cap.
    /// </summary>
    public long? LengthLimit { get; set; }

    /// <summary>
    /// <see langword="true"/> once the next boundary delimiter has been
    /// observed (and the section body fully drained).
    /// </summary>
    public bool FinishedSection => _finished;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _observedLength;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadCore(buffer.AsSpan(offset, count));
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return 0;
        }

        // Ensure enough lookahead to detect a boundary that straddles a chunk
        // boundary: we need (boundary.length + 2) bytes so the trailing "--"
        // of a closing delimiter is visible inside the buffer.
        int required = _boundary.BoundaryBytes.Length + 2;
        bool any = await _inner.EnsureBufferedAsync(required, cancellationToken).ConfigureAwait(false);

        return ReadCore(buffer.AsSpan(offset, count), allowShortRead: !any);
    }

    private int ReadCore(Span<byte> destination, bool allowShortRead = false)
    {
        if (_finished || destination.IsEmpty)
        {
            return 0;
        }

        ArraySegment<byte> buffered = _inner.BufferedData;
        if (buffered.Count == 0)
        {
            // The async path filled the buffer before calling us. The sync
            // path falls back here if it was invoked without pre-filling.
            if (allowShortRead)
            {
                _finished = true;
                return 0;
            }

            // Best-effort: synchronously top up.
            if (!_inner.EnsureBuffered(_boundary.BoundaryBytes.Length + 2))
            {
                // End of underlying stream before boundary — treat as truncated
                // section.
                _finished = true;
                return 0;
            }

            buffered = _inner.BufferedData;
        }

        int safeLength = FindReadableLength(buffered);
        if (safeLength == 0)
        {
            // The buffer starts with a boundary match — consume it and stop.
            ConsumeBoundary(buffered);
            return 0;
        }

        int take = Math.Min(safeLength, destination.Length);
        buffered.AsSpan(0, take).CopyTo(destination);
        _inner.SkipBuffered(take);
        _position += take;
        _observedLength += take;

        if (LengthLimit is long cap && _observedLength > cap)
        {
            throw new InvalidDataException($"Multipart section body exceeded the {cap}-byte limit.");
        }

        return take;
    }

    /// <summary>
    /// Returns the number of bytes at the head of <paramref name="buffered"/>
    /// that are safe to yield to the caller (i.e. cannot possibly be part of
    /// a boundary). Returns 0 when the buffer head matches the boundary
    /// prefix (the caller drains the boundary in that case).
    /// </summary>
    private int FindReadableLength(ArraySegment<byte> buffered)
    {
        byte[] bytes = buffered.Array!;
        int start = buffered.Offset;
        int end = start + buffered.Count;
        byte[] needle = _boundary.BoundaryBytes;
        int needleLen = needle.Length;

        for (int i = start; i < end; i++)
        {
            int matchLen = MatchLength(bytes, i, end, needle);
            if (matchLen == needleLen)
            {
                // Full match starting at i. The bytes before i are safe to
                // yield; the boundary itself is left in the buffer for the
                // boundary-consumer path.
                return i - start;
            }

            // Partial match that runs off the end of the buffer? Stop now so
            // the next refill can decide. We yield the bytes preceding this
            // potential match.
            if (matchLen > 0 && i + matchLen == end)
            {
                return i - start;
            }
        }

        // No (partial or full) boundary in buffer — everything before the
        // last (needleLen - 1) bytes is safe.
        return Math.Max(0, buffered.Count - (needleLen - 1));
    }

    private static int MatchLength(byte[] bytes, int start, int end, byte[] needle)
    {
        int n = 0;
        while (n < needle.Length && start + n < end && bytes[start + n] == needle[n])
        {
            n++;
        }
        return n;
    }

    private void ConsumeBoundary(ArraySegment<byte> buffered)
    {
        byte[] needle = _boundary.BoundaryBytes;

        // Drain the boundary itself.
        _inner.SkipBuffered(needle.Length);

        // Check whether this is the closing delimiter: "--" follows the
        // boundary bytes. We need 2 more bytes guaranteed in the buffer.
        if (!_inner.EnsureBuffered(2))
        {
            _finished = true;
            _boundary.FinalBoundaryFound = true;
            return;
        }

        ArraySegment<byte> rest = _inner.BufferedData;
        if (rest.Count >= 2 && rest.Array![rest.Offset] == (byte)'-' && rest.Array[rest.Offset + 1] == (byte)'-')
        {
            _inner.SkipBuffered(2);
            _boundary.FinalBoundaryFound = true;
        }

        // Consume the CRLF that follows a non-closing delimiter (RFC 2046).
        // If we just matched the closing delimiter the trailing octets are
        // ignored by the parser — they belong to the epilogue, which we drop.
        ConsumeOptionalCrlf();

        _finished = true;
    }

    private void ConsumeOptionalCrlf()
    {
        if (!_inner.EnsureBuffered(2))
        {
            return;
        }

        ArraySegment<byte> rest = _inner.BufferedData;
        if (rest.Count >= 2 && rest.Array![rest.Offset] == 0x0D && rest.Array[rest.Offset + 1] == 0x0A)
        {
            _inner.SkipBuffered(2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // The inner buffered stream is owned by the MultipartReader; do not
        // dispose it from a section stream.
    }
}
