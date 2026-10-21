using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

/// <summary>
/// Writes each complete frame in one stream write. The underlying stream can flush
/// on write; callers can also explicitly flush through this writer.
/// </summary>
/// <remarks>
/// This is the writer that touches the transport, so it is the one that writes each frame it wrote
/// to the <c>Assimalign.Cohesion.Database.Protocol</c> event source (<c>FrameWritten</c>, under the
/// <c>Frames</c> keyword): every other writer decorates one of these through its public member, so
/// each wire frame is reported once. The core is already asynchronous, so the trace adds one
/// enabled check per frame and nothing else while no listener takes it.
/// </remarks>
internal sealed class ProtocolStreamFrameWriter : ProtocolFrameWriter
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _header = new byte[ProtocolFrameHeader.Size];
    private readonly int _maxBatchBytes;
    private readonly int _maxBatchFrames;
    private byte[]? _batch;
    private int _batchLength;
    private int _batchFrames;
    private bool _disposed;

    internal ProtocolStreamFrameWriter(Stream stream, bool leaveOpen, int maxBatchBytes = 0, int maxBatchFrames = 1)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _maxBatchBytes = maxBatchBytes;
        _maxBatchFrames = maxBatchFrames;
    }

    /// <inheritdoc />
    protected override async ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        int length = ProtocolFrameHeader.Size + frame.Payload.Length;
        if (_maxBatchBytes != 0)
        {
            if (_batchLength > _maxBatchBytes - length)
            {
                await SendBatchAsync(cancellationToken).ConfigureAwait(false);
            }
            if (length <= _maxBatchBytes)
            {
                _batch ??= ProtocolBufferPool.Rent(_maxBatchBytes);
                frame.Header.WriteTo(_batch.AsSpan(_batchLength));
                frame.Payload.CopyTo(_batch.AsMemory(_batchLength + ProtocolFrameHeader.Size));
                _batchLength += length;
                if (++_batchFrames == _maxBatchFrames || _batchLength == _maxBatchBytes)
                {
                    await SendBatchAsync(cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }
        // The payload bound is checked by the base's public WriteFrameAsync before this core runs
        // (owner decision 29 of 2026-10-06), so every frame that reaches here is within it.
        if (frame.Payload.IsEmpty)
        {
            frame.Header.WriteTo(_header);
            ProtocolEventSource.Log.TransportWrite();
            await _stream.WriteAsync(_header, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            byte[] buffer = ProtocolBufferPool.Rent(length);
            try
            {
                frame.Header.WriteTo(buffer);
                frame.Payload.CopyTo(buffer.AsMemory(ProtocolFrameHeader.Size));
                ProtocolEventSource.Log.TransportWrite();
                await _stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ProtocolBufferPool.Return(buffer);
            }
        }

        ProtocolEventSource.Log.FrameWritten(frame.Type, frame.Payload.Length);
    }

    /// <inheritdoc />
    protected override async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendBatchAsync(cancellationToken).ConfigureAwait(false);
        ProtocolEventSource.Log.TransportFlush();
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendBatchAsync(CancellationToken cancellationToken)
    {
        if (_batchLength == 0) { return; }
        int length = _batchLength;
        // One session flow owns the writer; no latch is held during transport backpressure.
        // The server joins its pump before disposal returns this buffer to the pool.
        _batchLength = 0;
        _batchFrames = 0;
        try
        {
            ProtocolEventSource.Log.TransportWrite();
            await _stream.WriteAsync(_batch!.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            int position = 0;
            while (position < length)
            {
                ProtocolFrameHeader.TryParse(_batch.AsSpan(position), out var header);
                ProtocolEventSource.Log.FrameWritten(header.Type, (int)header.PayloadLength);
                position += ProtocolFrameHeader.Size + (int)header.PayloadLength;
            }
        }
        finally
        {
            _batch!.AsSpan(0, length).Clear();
        }
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore()
    {
        if (_disposed) { return default; }
        _disposed = true;
        if (_batch is { } batch)
        {
            _batch = null;
            ProtocolBufferPool.Return(batch);
        }
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        return default;
    }
}
