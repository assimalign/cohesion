using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Client;

// ReceiveAsync owns the frame exchange until its verified completion. The caller reads
// through this one-slot queue instead of retaining the connection's reader or writer.
internal sealed class BlobDownloadStream : Stream
{
    private readonly CancellationTokenSource _operation;
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _completion = Task.CompletedTask;
    private ExceptionDispatchInfo? _failure;
    private ReadOnlyMemory<byte> _current;
    private int _disposed;
    private int _reading;

    internal BlobDownloadStream(CancellationTokenSource operation)
    {
        _operation = operation;
        Destination = new DownloadDestination(this);
    }

    internal Task Started => _started.Task;
    internal Stream Destination { get; }
    internal CancellationToken CancellationToken => _operation.Token;
    internal void SetCompletion(Task completion) => _completion = completion;
    internal void SignalStarted() => _started.TrySetResult();

    internal void Complete(Exception? failure)
    {
        if (failure is not null)
        {
            Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(failure));
            _started.TrySetException(failure);
        }
        _chunks.Writer.TryComplete();
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _reading, 1) != 0)
        {
            throw new InvalidOperationException("Concurrent reads are not supported on a Blob download stream.");
        }
        try
        {
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(), _operation);
            ThrowIfFailed();
            if (buffer.Length == 0)
            {
                return 0;
            }
            while (_current.IsEmpty)
            {
                if (!await _chunks.Reader.WaitToReadAsync(_operation.Token).ConfigureAwait(false))
                {
                    // A clean queue alone is not EOF: the producer must have consumed and
                    // verified TransferComplete, released its exchange, and recorded no error.
                    await _completion.ConfigureAwait(false);
                    ThrowIfFailed();
                    return 0;
                }
                if (_chunks.Reader.TryRead(out byte[]? chunk))
                {
                    _current = chunk;
                }
                ThrowIfFailed();
            }
            int count = Math.Min(buffer.Length, _current.Length);
            _current.Span[..count].CopyTo(buffer.Span);
            _current = _current[count..];
            return count;
        }
        catch (OperationCanceledException)
        {
            // Cancellation must finish closing the wire before returning to the caller.
            await _completion.ConfigureAwait(false);
            ThrowIfFailed();
            throw;
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (!_completion.IsCompleted)
        {
            await _operation.CancelAsync().ConfigureAwait(false);
        }
        await _completion.ConfigureAwait(false);
        _current = default;
        while (_chunks.Reader.TryRead(out _)) { }
        _operation.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfFailed()
    {
        Volatile.Read(ref _failure)?.Throw();
        _operation.Token.ThrowIfCancellationRequested();
    }

    private sealed class DownloadDestination(BlobDownloadStream owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Protocol frame payload lifetime belongs to the frame reader. Copy just this
            // bounded chunk, and wait when the consumer already has a queued chunk.
            return owner._chunks.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }
    }
}
