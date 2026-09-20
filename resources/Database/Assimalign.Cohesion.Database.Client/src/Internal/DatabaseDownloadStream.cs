using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

// The worker owns all frame I/O. Only bounded copies of content cross into the caller's stream.
internal sealed class DatabaseDownloadStream : Stream
{
    private const int chunkSize = 64 * 1024;
    private readonly IDatabaseConnection _connection;
    private readonly bool _ownsConnection;
    private readonly CancellationTokenSource _operation;
    private readonly CancellationToken _cancellationToken;
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releasedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _completion = Task.CompletedTask;
    private ExceptionDispatchInfo? _failure;
    private ReadOnlyMemory<byte> _current;
    private int _disposed;
    private int _released;
    private int _reading;

    private DatabaseDownloadStream(IDatabaseConnection connection, CancellationToken cancellationToken, bool ownsConnection)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationToken = _operation.Token;
    }

    internal static async ValueTask<Stream> CreateAsync(IDatabaseConnection connection,
        IDatabaseStreamingExchange exchange, CancellationToken cancellationToken, bool ownsConnection = false)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(exchange);
            if (!ReferenceEquals(connection.Family, exchange.Family))
            {
                throw new ArgumentException("The exchange belongs to a different message family.", nameof(exchange));
            }
            if (!connection.IsOpen)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal, "The connection is not open.");
            }
        }
        catch
        {
            if (ownsConnection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }

        var stream = new DatabaseDownloadStream(connection, cancellationToken, ownsConnection);
        stream._completion = stream.RunAsync(exchange);
        try
        {
            await stream._started.Task.ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

    public override int Read(Span<byte> buffer)
    {
        // Stream's default span implementation may rent the caller's entire requested length.
        byte[] temporary = new byte[Math.Min(buffer.Length, chunkSize)];
        int count = Read(temporary, 0, temporary.Length);
        temporary.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _reading, 1) != 0)
        {
            throw new InvalidOperationException("Concurrent reads are not supported on a database download stream.");
        }
        try
        {
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((DatabaseDownloadStream)state!).Cancel(), this);
            ThrowIfFailed();
            if (buffer.Length == 0)
            {
                return 0;
            }
            while (_current.IsEmpty)
            {
                if (!await _chunks.Reader.WaitToReadAsync(_cancellationToken).ConfigureAwait(false))
                {
                    // Queue completion becomes EOF only after terminal verification and lease cleanup.
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
            // The caller observes cancellation only after the worker has unwound and closed its wire.
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

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = DisposeCoreAsync();
        }
        return new ValueTask(_disposedCompletion.Task);
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            if (!_completion.IsCompleted)
            {
                await _operation.CancelAsync().ConfigureAwait(false);
            }
            await _completion.ConfigureAwait(false);
            if (_ownsConnection && Volatile.Read(ref _failure) is null)
            {
                await ReleaseConnectionAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _current = default;
            while (_chunks.Reader.TryRead(out _)) { }
            _operation.Dispose();
            GC.SuppressFinalize(this);
        }
        if (failure is null)
        {
            _disposedCompletion.TrySetResult();
        }
        else
        {
            _disposedCompletion.TrySetException(failure);
        }
    }

    private async Task RunAsync(IDatabaseStreamingExchange exchange)
    {
        var operation = new StreamingExchange(this, exchange);
        try
        {
            await _connection.ExecuteAsync(operation, _cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Preserve the operation's original failure even if teardown also fails.
            if (operation.Entered || _ownsConnection)
            {
                try
                {
                    await ReleaseConnectionAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The diagnostic remains the exchange failure if returning its broken lease fails.
                }
            }
            Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(exception));
            _started.TrySetException(exception);
        }
        finally
        {
            _chunks.Writer.TryComplete();
        }
    }

    private async ValueTask ReleaseConnectionAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _releasedCompletion.TrySetResult();
            }
            catch (Exception exception)
            {
                _releasedCompletion.TrySetException(exception);
            }
        }
        await _releasedCompletion.Task.ConfigureAwait(false);
    }

    private void Cancel()
    {
        try
        {
            _operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A simultaneous disposal already joined the producer and released the operation source.
        }
    }

    private void ThrowIfFailed()
    {
        Volatile.Read(ref _failure)?.Throw();
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class StreamingExchange(DatabaseDownloadStream owner, IDatabaseStreamingExchange exchange)
        : IDatabaseProtocolExchange<bool>
    {
        public ProtocolMessageFamily Family => exchange.Family;
        internal bool Entered { get; private set; }

        public async ValueTask<bool> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
            CancellationToken cancellationToken = default)
        {
            Entered = true;
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((DatabaseDownloadStream)state!).Cancel(), owner);
            var streamingReader = new ClientFrameReader(reader);
            var streamingWriter = new ClientFrameWriter(writer);
            await exchange.OpenAsync(streamingReader, streamingWriter, cancellationToken).ConfigureAwait(false);
            owner._started.TrySetResult();
            // Model code may use synchronous destination writes. Let stream creation return before
            // such a write can block on the bounded queue waiting for its first consumer.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            await exchange.CopyToAsync(streamingReader, streamingWriter, new DownloadDestination(owner), cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private sealed class DownloadDestination(DatabaseDownloadStream owner) : Stream
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

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, chunkSize);
                WriteAsync(buffer[..count].ToArray()).AsTask().GetAwaiter().GetResult();
                buffer = buffer[count..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((DatabaseDownloadStream)state!).Cancel(), owner);
            CancellationToken operationToken = owner._cancellationToken;
            operationToken.ThrowIfCancellationRequested();
            while (!buffer.IsEmpty)
            {
                // Wait before copying, so even a model's very large write cannot grow queued content.
                if (!await owner._chunks.Writer.WaitToWriteAsync(operationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The download content destination is complete.");
                }
                int count = Math.Min(buffer.Length, chunkSize);
                await owner._chunks.Writer.WriteAsync(buffer[..count].ToArray(), operationToken).ConfigureAwait(false);
                buffer = buffer[count..];
            }
        }
    }
}
