using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Storage.Internal;

internal sealed class DocumentReadStream : Stream
{
    private readonly DocumentStorage _storage;
    private readonly DocumentContentReference _content;
    private readonly Func<ValueTask>? _onDispose;
    private ReadOnlyMemory<byte> _buffer;
    private ulong _next;
    private long _position;
    private uint _checksum = uint.MaxValue;
    private bool _disposed;

    internal DocumentReadStream(DocumentStorage storage, DocumentContentReference content, Func<ValueTask>? onDispose)
    {
        if (content.Length < 0 || (content.Head == 0) != (content.Length == 0))
        {
            throw new StorageCorruptionException("Invalid document content reference.");
        }

        _storage = storage;
        _content = content;
        _onDispose = onDispose;
        _next = content.Head;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _content.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = 0;
        while (!buffer.IsEmpty && _position < _content.Length)
        {
            if (_buffer.IsEmpty)
            {
                var (page, slot) = DocumentStorage.UnpackLocation(_next);
                var chunk = DocumentChunkCodec.Decode(_storage.ReadEntry(page, slot), _content.Length - _position);
                _next = chunk.Next;
                _buffer = chunk.Payload;
            }
            int count = Math.Min(_buffer.Length, buffer.Length);
            var bytes = _buffer.Span[..count];
            bytes.CopyTo(buffer);
            _checksum = DocumentContentChecksum.Append(_checksum, bytes);
            _buffer = _buffer[count..];
            buffer = buffer[count..];
            _position += count;
            total += count;
        }
        if (_position == _content.Length && ~_checksum != _content.Checksum)
        {
            throw new StorageCorruptionException("Document content checksum disagrees with its catalog metadata.");
        }

        return total;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async ValueTask CloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer = default;
        if (_onDispose is not null)
        {
            await _onDispose().ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

