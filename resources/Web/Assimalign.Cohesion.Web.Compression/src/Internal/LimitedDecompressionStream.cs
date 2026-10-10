using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// A read-only wrapper over a request-body decode chain that counts the decompressed bytes returned
/// and throws <see cref="RequestDecompressionLimitException"/> once the running total exceeds the
/// configured guard. This is the zip-bomb defense: the transport caps only the compressed wire
/// bytes, so the decompressed output must be bounded here.
/// </summary>
/// <remarks>
/// <para>
/// The guard is enforced lazily, as the handler reads &#8212; a body is never fully inflated up
/// front. Because each decoder in a multi-coding chain pulls only what the layer above it consumes,
/// bounding the final read also bounds the work and memory of every intermediate layer. Disposing
/// this stream disposes the decode chain but leaves the underlying transport body open (the transport
/// owns its lifetime).
/// </para>
/// <para>
/// A decoder that rejects malformed coded content throws <see cref="InvalidDataException"/>, which
/// this stream relabels as <see cref="RequestDecompressionFormatException"/> so the middleware answers
/// <c>400</c>. The transport body under the decoders throws the same exception type when the message
/// framing is malformed (a broken chunk-size line, for example), and that failure is not the coded
/// content's: when the server reports a client fault for the exchange (<see cref="IWebClientFaultFeature"/>),
/// the exception passes through unchanged, so the code above classifies it as the client fault it is
/// and the transport answers with its own status (#1340).
/// </para>
/// </remarks>
internal sealed class LimitedDecompressionStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly IWebClientFaultFeature? _clientFault;
    private long _total;

    public LimitedDecompressionStream(Stream inner, long limit, IWebClientFaultFeature? clientFault)
    {
        _inner = inner;
        _limit = limit;
        _clientFault = clientFault;
    }

    // Read when a decoder's read failed: the transport latches the client fault before it throws.
    private bool IsTransportClientFault => _clientFault?.StatusCode is not null;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read;
        try
        {
            read = _inner.Read(buffer, offset, count);
        }
        catch (InvalidDataException exception) when (!IsTransportClientFault)
        {
            // A decoder rejected malformed coded content: surface it as a typed 400 signal rather
            // than letting a raw InvalidDataException escape into the pipeline as a 500. A malformed
            // message framing under the decoders is the transport's client fault and passes through.
            throw new RequestDecompressionFormatException(exception);
        }

        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception) when (!IsTransportClientFault)
        {
            throw new RequestDecompressionFormatException(exception);
        }

        Count(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Count(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _total += read;
        if (_total > _limit)
        {
            throw new RequestDecompressionLimitException(_limit);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
