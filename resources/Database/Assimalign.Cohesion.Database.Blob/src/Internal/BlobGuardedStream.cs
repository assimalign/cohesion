using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// The stream a <see cref="BlobContainer"/> hands out: it keeps the operation's read snapshot
/// pinned until disposal and refuses access after the session's rollback or disposal, including
/// bytes already buffered by the underlying stream.
/// </summary>
/// <remarks>
/// A read that fails once the operation started (a canceled ReadAsync, a checksum mismatch, a
/// storage error) fails the operation, so inside an explicit transaction it aborts the
/// transaction like any other failed operation (#1225). An upload's failures reach the operation
/// through the storage stream's own abort callback.
/// </remarks>
internal sealed class BlobGuardedStream : Stream
{
    private readonly Stream _inner;
    private readonly BlobOperation _operation;
    private bool _disposed;
    private bool _readFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobGuardedStream"/> class.
    /// </summary>
    /// <param name="inner">The underlying blob content stream.</param>
    /// <param name="operation">The blob operation whose transaction must stay active for the stream to be used.</param>
    public BlobGuardedStream(Stream inner, BlobOperation operation)
    {
        _inner = inner;
        _operation = operation;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _inner.CanWrite;
    public override long Length { get { Check(); return _inner.Length; } }
    public override long Position { get { Check(); return _inner.Position; } set => throw new NotSupportedException(); }

    // An upload's write, flush and completion failures already ended the operation through the
    // storage stream's abort callback; what reaches the caller is translated the way every other
    // operation's failure is (#1243): the offline storage's coded refusal, or the area root's
    // exception for a kernel failure such as the upload's unconfirmed commit.
    public override void Flush()
    {
        Check();
        try
        {
            _inner.Flush();
        }
        catch (Exception error)
        {
            var reported = _operation.TranslateFailure(error);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        Check();
        try
        {
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var reported = _operation.TranslateFailure(error);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        CheckReadable();
        try
        {
            return _inner.Read(buffer);
        }
        catch (Exception error)
        {
            _readFailed = true;
            var reported = _operation.TranslateFailure(error);
            _operation.AbortAsync(reported).AsTask().GetAwaiter().GetResult();
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckReadable();
        try
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _readFailed = true;
            var reported = _operation.TranslateFailure(error);
            await _operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check();
        try
        {
            _inner.Write(buffer);
        }
        catch (Exception error)
        {
            var reported = _operation.TranslateFailure(error);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check();
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var reported = _operation.TranslateFailure(error);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); _operation.EnsureActive(); }

    // A refused read (an ended operation, an upload stream) is not a failure of the
    // operation and leaves it as it is.
    private void CheckReadable()
    {
        Check();
        if (!_inner.CanRead)
        {
            throw new NotSupportedException("The blob stream does not support reading.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _inner.Dispose(); }
            catch (Exception error) when (!_readFailed)
            {
                var reported = _operation.TranslateFailure(error);
                _operation.AbortAsync(reported).AsTask().GetAwaiter().GetResult();
                if (ReferenceEquals(reported, error)) { throw; }
                throw reported;
            }
            catch (Exception error) when (_readFailed)
            {
                // The failed read already ended the operation and threw to the caller; the
                // stream's completion callback can only report that the operation ended. The
                // stream serves in-process callers too, so it knows no session, container or blob.
                BlobDatabaseEventSource.Log.TransferFailed(null, string.Empty, code: null, error);
            }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (!_readFailed)
        {
            var reported = _operation.TranslateFailure(error);
            await _operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
        catch (Exception error) when (_readFailed)
        {
            // See Dispose: the failed read already ended the operation and threw to the caller.
            BlobDatabaseEventSource.Log.TransferFailed(null, string.Empty, code: null, error);
        }
        GC.SuppressFinalize(this);
    }
}
