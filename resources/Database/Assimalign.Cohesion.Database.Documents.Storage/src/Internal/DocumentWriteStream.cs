using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Storage.Internal;

internal sealed class DocumentWriteStream : Stream
{
    private readonly DocumentStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;
    private readonly Func<DocumentContentReference, ValueTask> _complete;
    private readonly Func<ValueTask> _abort;
    private readonly CancellationToken _lifetimeCancellation;
    private readonly byte[] _buffer = new byte[DocumentChunkCodec.PayloadSize];
    private int _buffered;
    private long _length;
    private ulong _head;
    private ulong _tail;
    private uint _checksum = uint.MaxValue;
    private bool _disposed;
    private bool _failed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentWriteStream"/> class.
    /// </summary>
    /// <param name="storage">The document storage that receives the uploaded chunks.</param>
    /// <param name="coordinator">The database's coordinator.</param>
    /// <param name="context">The logical transaction spanning all chunks and metadata.</param>
    /// <param name="complete">Publishes metadata and, for an automatic transaction, commits after successful disposal.</param>
    /// <param name="abort">Aborts the logical transaction after any upload failure.</param>
    /// <param name="lifetimeCancellation">Cancellation retained for the upload's entire lifetime.</param>
    public DocumentWriteStream(
        DocumentStorage storage,
        TransactionCoordinator coordinator,
        ITransactionContext context,
        Func<DocumentContentReference, ValueTask> complete,
        Func<ValueTask> abort,
        CancellationToken lifetimeCancellation)
    {
        _storage = storage;
        _coordinator = coordinator;
        _context = context;
        _complete = complete;
        _abort = abort;
        _lifetimeCancellation = lifetimeCancellation;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_failed;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        try
        {
            while (!buffer.IsEmpty)
            {
                _lifetimeCancellation.ThrowIfCancellationRequested();
                EnsureTransaction();
                int count = Math.Min(_buffer.Length - _buffered, buffer.Length);
                var content = buffer[..count];
                content.CopyTo(_buffer.AsSpan(_buffered));
                _checksum = DocumentContentChecksum.Append(_checksum, content);
                _buffered += count;
                _length = checked(_length + count);
                buffer = buffer[count..];
                if (_buffered == _buffer.Length)
                {
                    PersistBufferAsync(default).AsTask().GetAwaiter().GetResult();
                }
            }
        }
        catch
        {
            FailAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lifetimeCancellation.ThrowIfCancellationRequested();
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lifetimeCancellation.ThrowIfCancellationRequested();
                EnsureTransaction();
                int count = Math.Min(_buffer.Length - _buffered, buffer.Length);
                var content = buffer[..count];
                content.CopyTo(_buffer.AsMemory(_buffered));
                _checksum = DocumentContentChecksum.Append(_checksum, content.Span);
                _buffered += count;
                _length = checked(_length + count);
                buffer = buffer[count..];
                if (_buffered == _buffer.Length)
                {
                    await PersistBufferAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await FailAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => FlushAsync(default).GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureWritable();
        try
        {
            await PersistBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await FailAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask PersistBufferAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lifetimeCancellation.ThrowIfCancellationRequested();
        EnsureTransaction();
        if (_buffered == 0)
        {
            return;
        }

        var record = DocumentChunkCodec.Encode(_buffer.AsSpan(0, _buffered), _context.Sequence);
        ulong next = await _coordinator.ApplyStatementAsync(_context, bracket =>
        {
            var (page, slot) = _storage.InsertChunk(bracket, _context.Sequence, record);
            ulong location = DocumentStorage.PackLocation(page, slot);
            if (_tail != 0)
            {
                var (tailPage, tailSlot) = DocumentStorage.UnpackLocation(_tail);
                var tail = _storage.ReadEntry(tailPage, tailSlot).ToArray();
                DocumentChunkCodec.WriteNext(tail, location);
                _storage.UpdateEntry(bracket, tailPage, tailSlot, tail);
            }
            _coordinator.VersionStore.RecordCreated(_context.Sequence, page, slot);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        if (_head == 0)
        {
            _head = next;
        }

        _tail = next;
        _buffered = 0;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed)
        {
            throw new InvalidOperationException("The document upload has failed and cannot be published.");
        }
    }

    private void EnsureTransaction()
    {
        if (_context.State != TransactionState.Active)
        {
            throw new InvalidOperationException("The document upload's transaction is no longer active.");
        }
    }

    private async ValueTask FailAsync()
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        await _abort().ConfigureAwait(false);
    }

    private async ValueTask CompleteAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_failed)
        {
            return;
        }

        try
        {
            await PersistBufferAsync(default).ConfigureAwait(false);
            _lifetimeCancellation.ThrowIfCancellationRequested();
            await _complete(new DocumentContentReference(_head, _length, ~_checksum)).ConfigureAwait(false);
        }
        catch
        {
            await FailAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

