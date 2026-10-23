using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

[global::System.Diagnostics.DebuggerDisplay("BmffWriterDefault")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffWriterDefault : BmffWriter
{
    private readonly int _bufferLimit;
    private readonly int _maxDepth;
    private readonly Stack<(BmffStream Parent, BmffBufferStream? Buffer, BmffBoxType Type, Guid? ExtendedType)> _containers = new();
    private BmffStream _output;
    private bool _disposed;
    private bool _ended;

    internal BmffWriterDefault(Stream stream, int bufferLimit, int maxDepth)
    {
        _bufferLimit = bufferLimit;
        _maxDepth = maxDepth;
        var offset = stream is BmffStream window ? window.Position : stream.CanSeek ? stream.Position : 0;
        var length = stream is BmffStream bounded ? bounded.Remaining : long.MaxValue - offset;
        _output = new BmffStream(stream, offset, length);
    }

    internal override int DocumentBufferLimit => _bufferLimit;
    internal override int DocumentMaxDepth => _maxDepth;

    private void CheckSize(long size)
    {
        if (size > _output.Remaining)
        {
            throw new ContentFormatException(ErrorMessages.WindowExceeded, ContentFormatErrorCode.InvalidStructure, _output.Offset + _output.Position);
        }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ended)
        {
            throw new InvalidOperationException(ErrorMessages.WriterClosed);
        }
    }

    public override void Write(BmffBox box)
    {
        EnsureWritable();
        var plan = BmffWritePlan.Create(box, _containers.Count, _maxDepth, _containers.Count == 0);
        CheckSize(plan.Size);
        plan.Write(_output);
        _ended = plan.Header.ExtendsToEnd;
    }

    public override async ValueTask WriteAsync(BmffBox box, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        cancellationToken.ThrowIfCancellationRequested();
        var plan = BmffWritePlan.Create(box, _containers.Count, _maxDepth, _containers.Count == 0);
        CheckSize(plan.Size);
        await plan.WriteAsync(_output, _bufferLimit, cancellationToken).ConfigureAwait(false);
        _ended = plan.Header.ExtendsToEnd;
    }

    public override void BeginContainer(BmffBoxType type, long? payloadSize = null, Guid? extendedType = null)
    {
        EnsureWritable();
        if (_containers.Count > _maxDepth)
        {
            throw new ContentFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, _output.Offset + _output.Position);
        }
        if (payloadSize.HasValue)
        {
            var header = BmffBoxHeader.Create(type, payloadSize.Value, extendedType: extendedType);
            CheckSize(header.TotalSize!.Value);
            header.Write(_output);
            var parent = _output;
            _output = new BmffStream(parent, parent.Position, payloadSize.Value);
            _containers.Push((parent, null, type, extendedType));
        }
        else
        {
            // Validate the type/UUID pair before any producer writes.
            _ = BmffBoxHeader.Create(type, 0, extendedType: extendedType);
            var buffer = new BmffBufferStream(_bufferLimit);
            var parent = _output;
            _output = new BmffStream(buffer, 0, long.MaxValue);
            _containers.Push((parent, buffer, type, extendedType));
        }
    }

    public override void WritePayload(BinarySegment payload)
    {
        EnsureWritable();
        if (_containers.Count == 0)
        {
            throw new InvalidOperationException(ErrorMessages.ContainerState);
        }
        payload.CopyTo(_output);
    }

    public override void EndContainer()
    {
        EnsureWritable();
        if (_containers.Count == 0)
        {
            throw new InvalidOperationException(ErrorMessages.ContainerState);
        }
        var state = _containers.Peek();
        if (state.Buffer is null && _output.Position != _output.Length)
        {
            throw new ContentFormatException(ErrorMessages.IncompleteBox, ContentFormatErrorCode.InvalidStructure, _output.Offset + _output.Position);
        }
        _containers.Pop();
        var size = _output.Position;
        _output.Dispose();
        _output = state.Parent;
        if (state.Buffer is not null)
        {
            using var buffer = state.Buffer;
            BmffBoxHeader.Create(state.Type, size, extendedType: state.ExtendedType).Write(_output);
            _output.Write(buffer.GetBuffer().AsSpan(0, (int)size));
        }
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _output.Dispose();
            while (_containers.TryPop(out var state))
            {
                state.Buffer?.Dispose();
                state.Parent.Dispose();
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(BmffWriterDefault value)
        {
        }
    }
}
