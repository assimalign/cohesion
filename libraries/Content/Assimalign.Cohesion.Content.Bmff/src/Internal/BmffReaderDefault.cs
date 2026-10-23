using System;
using System.Buffers;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

[global::System.Diagnostics.DebuggerDisplay("BmffReaderDefault")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffReaderDefault : BmffReader
{
    private readonly BmffStream _stream;
    private readonly bool _topLevel;
    private readonly int? _expectedChildren;
    private BmffBoxHeader _header;
    private BmffStream? _payload;
    private BmffBox? _current;
    private BmffReader? _child;
    private bool _disposed;
    private bool _consumed;
    private bool _ended;
    private int _childCount;

    internal BmffReaderDefault(BmffStream stream, bool topLevel = false, int? expectedChildren = null)
    {
        _stream = stream;
        _topLevel = topLevel;
        _expectedChildren = expectedChildren;
    }

    private bool IsSampleEntry => _stream.ChildContext == BmffBoxType.SampleDescription;
    private bool IsTrackReference => _stream.ChildContext == BmffBoxType.TrackReference && (uint)_header.BoxType is
        0x68696e74 or 0x63647363 or 0x68696e64 or 0x64706e64 or 0x69706972 or 0x6d706f64 or 0x73796e63;
    private bool CanParse => IsTrackReference || _stream.Registry.CanParse(_header.BoxType, IsSampleEntry);

    internal override BmffReaderOptions DocumentOptions => _stream.Options;
    internal override BmffBoxRegistry DocumentRegistry => _stream.Registry;
    internal void SetCancellationToken(System.Threading.CancellationToken cancellationToken) => _stream.CancellationToken = cancellationToken;

    public override BmffBoxHeader Header { get { EnsureCurrent(); return _header; } }
    public override Stream Payload { get { EnsureCurrent(); return _payload!; } }
    public override BmffBox Current
    {
        get
        {
            EnsureCurrent();
            if (_current is not null)
            {
                return _current;
            }
            if (_consumed || _payload!.Position != 0)
            {
                throw new InvalidOperationException(ErrorMessages.PayloadConsumed);
            }
            if (!CanParse)
            {
                return _current = Preserve(ContentFormatErrorCode.Unsupported);
            }
            var box = IsTrackReference ? new TrackReferenceTypeBox(_header) : _stream.Registry.Create(_header, IsSampleEntry)!;
            try
            {
                if (box is BmffBoxComposite || box is BmffBoxLazyLeaf || box is MediaDataBox || box is FreeSpaceBox || box is SkipBox)
                {
                    box.Read(_payload);
                }
                else
                {
                    if (!_header.TotalSize.HasValue || _payload.Length > _stream.Options.MaxBufferedPayloadSize)
                    {
                        throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, _header.Offset);
                    }
                    var length = (int)_payload.Length;
                    var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                    try
                    {
                        BmffBinary.ReadField(_payload, bytes.AsSpan(0, length));
                        using var memory = new MemoryStream(bytes, 0, length, writable: false);
                        using var input = new BmffStream(memory, 0, length)
                        {
                            AbsoluteOrigin = _header.Offset + _header.HeaderSize,
                            Options = _stream.Options,
                            Registry = _stream.Registry,
                            Depth = _stream.Depth
                        };
                        box.Read(input);
                        if (input.Remaining != 0)
                        {
                            throw new ContentFormatException(ErrorMessages.UnconsumedPayload, ContentFormatErrorCode.InvalidStructure, input.Offset + input.Position);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }
                }
                return _current = box;
            }
            catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Unsupported && !_stream.Options.Strict)
            {
                return _current = Preserve(error.ErrorCode);
            }
        }
    }

    private BmffBox Preserve(ContentFormatErrorCode reason)
    {
        if (_stream.Options.Strict && !IsSampleEntry)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedBox, reason, _header.Offset);
        }
        return UnknownBox.Create(_header, _payload!.GetSegment(0, _payload.Length), reason);
    }

    public override bool Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.CancellationToken.ThrowIfCancellationRequested();
        if (_ended)
        {
            return false;
        }
        if (_payload is not null)
        {
            Skip();
            _payload.Dispose();
            _payload = null;
            if (_ended)
            {
                return false;
            }
        }
        if (_expectedChildren.HasValue && _childCount == _expectedChildren.Value && _stream.ChildContext is BmffBoxType.DataReference or BmffBoxType.SampleDescription or BmffBoxType.ItemInfo)
        {
            _ended = true;
            return false;
        }
        _current = null;
        _consumed = false;
        var offset = _stream.Offset + _stream.Position;
        if (!BmffBoxHeader.TryRead(_stream, offset, _stream.HasKnownLength ? _stream.Offset + _stream.Length : null, _topLevel, out _header))
        {
            _ended = true;
            if (_expectedChildren.HasValue && _childCount != _expectedChildren)
            {
                throw new ContentFormatException(ErrorMessages.ChildCountMismatch, ContentFormatErrorCode.InvalidStructure, offset);
            }
            return false;
        }
        if (_stream.Options.Strict && !IsSampleEntry && !CanParse)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, offset);
        }
        _childCount++;
        if (_expectedChildren.HasValue && _childCount > _expectedChildren)
        {
            throw new ContentFormatException(ErrorMessages.ChildCountMismatch, ContentFormatErrorCode.InvalidStructure, offset);
        }
        if (_stream.Depth > _stream.Options.MaxDepth)
        {
            throw new ContentFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, offset);
        }
        _payload = new BmffStream(_stream, _stream.Position, _header.TotalSize - _header.HeaderSize ?? _stream.Remaining)
        {
            BoxType = _header.BoxType,
            HasKnownLength = _header.TotalSize.HasValue
        };
        return true;
    }

    public override BmffReader ReadChildren()
    {
        EnsureCurrent();
        if (_consumed || _payload!.Position != 0)
        {
            throw new InvalidOperationException(ErrorMessages.PayloadConsumed);
        }
        if (!_stream.Registry.CanParse(_header.BoxType, IsSampleEntry) || _stream.Registry.Create(_header, IsSampleEntry) is not BmffBoxComposite composite)
        {
            throw new ContentFormatException(ErrorMessages.NotContainer, ContentFormatErrorCode.Unsupported, _header.Offset);
        }
        var count = composite.ReadPrefix(_payload);
        _child = _payload.ReadChildrenCore(count);
        _consumed = true;
        return _child;
    }

    public override void Skip()
    {
        EnsureCurrent();
        _child?.Dispose();
        _child = null;
        if (_payload!.HasKnownLength)
        {
            try
            {
                _payload.SkipExactly(_payload.Remaining);
            }
            catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Truncated)
            {
                throw new ContentFormatException(ErrorMessages.TruncatedHeader, ContentFormatErrorCode.Truncated, _payload.Offset + _payload.Position);
            }
            if (_stream.CanSeek)
            {
                _stream.Position = _header.Offset - _stream.Offset + _header.TotalSize!.Value;
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                while (_payload.Read(buffer) != 0) { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            _ended = true;
        }
        _consumed = true;
    }

    private void EnsureCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_payload is null)
        {
            throw new InvalidOperationException(ErrorMessages.NoCurrentBox);
        }
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _child?.Dispose();
            _payload?.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class DebugView
    {
        public DebugView(BmffReaderDefault value)
        {
        }
    }
}
