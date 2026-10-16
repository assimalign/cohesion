using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>Reads EBML elements forwards over seekable or non-seekable streams.</summary>
/// <remarks>ReadNext returns false at the current master boundary. Call LeaveMaster, then ReadNext
/// to consume a sibling. Consume, skip, or enter each current element before advancing. The source
/// is borrowed by default. Instances and lazy segments sharing a source must be used sequentially.</remarks>
public sealed class EbmlReader : IDisposable
{
    private readonly Stream _source;
    private readonly EbmlInput _input;
    private readonly EbmlReaderOptions _options;
    private readonly bool _leaveOpen;
    private readonly List<MasterFrame> _masters = new();
    private readonly byte[] _headerBytes = new byte[16];
    private EbmlSchema _schema;
    private EbmlElementHeader? _pending;
    private EbmlElementHeader? _current;
    private bool _atEnd;
    private bool _disposed;
    private ulong _headerMaxId = 4;
    private ulong _headerMaxSize = 8;

    private EbmlReader(Stream source, EbmlSchema schema, EbmlReaderOptions options, bool leaveOpen)
    {
        _source = source;
        _input = new EbmlInput(source);
        _schema = schema;
        _options = options;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Creates a forward-only reader.</summary>
    /// <param name="source">The readable source, starting at its current cursor.</param>
    /// <param name="schema">A composed schema; null uses the RFC header schema.</param>
    /// <param name="options">Limits, copied at creation.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is unreadable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    public static EbmlReader Create(Stream source, EbmlSchema? schema = null, EbmlReaderOptions? options = null, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException(ErrorMessages.SourceReadable, nameof(source));
        }

        return new EbmlReader(source, schema ?? EbmlSchema.Header, (options ?? new EbmlReaderOptions()).Snapshot(), leaveOpen);
    }

    /// <summary>Gets the current header until its payload is consumed or entered.</summary>
    /// <exception cref="InvalidOperationException">No current element is available.</exception>
    public EbmlElementHeader Current => _current ?? throw new InvalidOperationException(ErrorMessages.ReaderState);
    /// <summary>Gets the absolute logical cursor, accounting for a buffered sibling header.</summary>
    public long Position => _pending?.Offset ?? _input.Position;
    /// <summary>Gets the number of entered masters.</summary>
    public int Depth => _masters.Count;

    /// <summary>Reads the next header at this level, without buffering its payload.</summary>
    /// <returns>False at the current master's end or clean root-level end.</returns>
    /// <exception cref="ContentFormatException">The header, size, or parent boundary is invalid.</exception>
    /// <exception cref="InvalidOperationException">The previous element has not been consumed.</exception>
    /// <exception cref="ObjectDisposedException">The reader is disposed.</exception>
    public bool ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null)
        {
            throw new InvalidOperationException(ErrorMessages.ReaderState);
        }

        if (_atEnd)
        {
            return false;
        }

        var boundary = Boundary();
        if (_pending is null && boundary is long end && _input.Position == end)
        {
            _atEnd = true;
            return false;
        }
        _input.End = boundary;
        EbmlElementHeader header;
        if (_pending is { } pending)
        {
            header = pending;
        }
        else
        {
            var start = _input.Position;
            if (!TryReadFirst())
            {
                if (boundary is long expected && _input.Position < expected)
                {
                    throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, _input.Position);
                }

                _atEnd = true;
                return false;
            }
            var id = ReadVInt(0, _options.MaxIdLength);
            if (!id.IsValidIdentifier)
            {
                throw new EbmlDataFormatException(ErrorMessages.InvalidVInt, ContentFormatErrorCode.InvalidStructure, start);
            }

            ReadExactly(_headerBytes.AsSpan(id.Length, 1));
            var size = ReadVInt(id.Length, _options.MaxSizeLength);
            var definition = _schema.Find(id);
            header = new EbmlElementHeader(id, size, start, definition?.Type ?? EbmlElementType.Binary);
        }

        if (_masters.Count > 0 && _masters[^1].Header.IsUnknownSize &&
            _schema.Find(header.Identifier) is { IsGlobal: false } next &&
            !next.AllowsParent(_masters[^1].Header.Identifier.EncodedValue))
        {
            _pending = header;
            _atEnd = true;
            return false;
        }
        _pending = null;
        if (header.DataSize is long length)
        {
            if (length > long.MaxValue - header.DataOffset ||
                (boundary is long limit && length > limit - header.DataOffset))
            {
                throw new EbmlDataFormatException(ErrorMessages.ParentBoundary, ContentFormatErrorCode.InvalidStructure, header.Offset);
            }

            if (_source.CanSeek && length > _source.Length - _source.Position)
            {
                throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, header.DataOffset);
            }
        }
        else if (header.Type != EbmlElementType.MasterElement || _schema.Find(header.Identifier)?.UnknownSizeAllowed != true)
        {
            throw new EbmlDataFormatException(ErrorMessages.UnknownSize, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }

        _current = header;
        return true;
    }

    /// <summary>Enters the current master without reading its children.</summary>
    /// <exception cref="InvalidOperationException">The current element is not a master.</exception>
    /// <exception cref="ContentFormatException">The depth limit is exceeded.</exception>
    public void EnterMaster()
    {
        var header = Current;
        if (header.Type != EbmlElementType.MasterElement)
        {
            throw new InvalidOperationException(ErrorMessages.InvalidType);
        }

        if (_masters.Count >= _options.MaxDepth)
        {
            throw new EbmlDataFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, header.Offset);
        }

        _masters.Add(new MasterFrame(header));
        _current = null;
        _atEnd = false;
    }

    /// <summary>Leaves a master after ReadNext has reported its end.</summary>
    /// <exception cref="InvalidOperationException">The master has not ended or none is entered.</exception>
    /// <exception cref="ContentFormatException">Header limits are unsupported.</exception>
    public void LeaveMaster()
    {
        if (!_atEnd || _current is not null || _masters.Count == 0)
        {
            throw new InvalidOperationException(ErrorMessages.ReaderState);
        }

        var frame = _masters[^1];
        _masters.RemoveAt(_masters.Count - 1);
        _atEnd = false;
        if (frame.Header.Identifier.EncodedValue == 0x1A45DFA3)
        {
            if (_headerMaxId is < 4 or > 8 || _headerMaxSize is < 1 or > 8)
            {
                throw new EbmlDataFormatException(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, frame.Header.Offset);
            }

            _options.MaxIdLength = Math.Min(_options.MaxIdLength, (int)_headerMaxId);
            _options.MaxSizeLength = Math.Min(_options.MaxSizeLength, (int)_headerMaxSize);
        }
    }

    /// <summary>Reads a leaf using its schema type. Unknown IDs return a BinarySegment.</summary>
    /// <returns>A ulong, long, double, string, or BinarySegment.</returns>
    /// <exception cref="ContentFormatException">The size, value, or buffering limit is invalid.</exception>
    /// <exception cref="InvalidOperationException">No leaf is current.</exception>
    public object ReadValue() => ReadValueCore(Current.Type);

    /// <summary>Reads an unsigned integer of zero through eight octets.</summary>
    /// <returns>The unsigned value.</returns>
    /// <exception cref="ContentFormatException">The type, length, or value is invalid.</exception>
    public ulong ReadUnsignedInteger() => (ulong)ReadValueCore(EbmlElementType.UnsignedInteger);
    /// <summary>Reads a two's-complement signed integer of zero through eight octets.</summary>
    /// <returns>The signed value.</returns>
    /// <exception cref="ContentFormatException">The type, length, or value is invalid.</exception>
    public long ReadSignedInteger() => (long)ReadValueCore(EbmlElementType.SignedInteger);
    /// <summary>Reads a float of zero, four, or eight octets.</summary>
    /// <returns>The floating value.</returns>
    /// <exception cref="ContentFormatException">The type, length, or value is invalid.</exception>
    public double ReadFloat() => (double)ReadValueCore(EbmlElementType.Float);
    /// <summary>Reads printable ASCII up to the first NUL, ignoring subsequent padding.</summary>
    /// <returns>The decoded string.</returns>
    /// <exception cref="ContentFormatException">The type, characters, or buffer limit is invalid.</exception>
    public string ReadAsciiString() => (string)ReadValueCore(EbmlElementType.AsciiString);
    /// <summary>Reads strict UTF-8 up to the first NUL, ignoring subsequent padding.</summary>
    /// <returns>The decoded string.</returns>
    /// <exception cref="ContentFormatException">The type, encoding, or buffer limit is invalid.</exception>
    public string ReadUtf8String() => (string)ReadValueCore(EbmlElementType.Utf8String);
    /// <summary>Reads signed nanoseconds since 2001-01-01T00:00:00 UTC without rounding.</summary>
    /// <returns>The exact nanosecond value.</returns>
    /// <exception cref="ContentFormatException">The type or size is invalid.</exception>
    public long ReadDate() => (long)ReadValueCore(EbmlElementType.Date);
    /// <summary>Returns a lazy binary range for seekable input; otherwise buffers within the configured cap.</summary>
    /// <returns>The binary value. Lazy values borrow the source.</returns>
    /// <exception cref="ContentFormatException">The type, size, or buffering limit is invalid.</exception>
    public BinarySegment ReadBinary() => (BinarySegment)ReadValueCore(EbmlElementType.Binary);

    /// <summary>Skips the current element, descending through masters without buffering binary values.</summary>
    /// <exception cref="ContentFormatException">A malformed child or a depth limit is encountered.</exception>
    /// <exception cref="InvalidOperationException">There is no current element.</exception>
    public void Skip()
    {
        var header = Current;
        if (header.Type == EbmlElementType.MasterElement)
        {
            EnterMaster();
            while (ReadNext())
            {
                Skip();
            }

            LeaveMaster();
        }
        else if (_masters.Count > 0 && _masters[^1].Header.Identifier.EncodedValue == 0x1A45DFA3 &&
            header.Type != EbmlElementType.Binary)
        {
            ReadValue();
        }
        else
        {
            SkipData(header.DataSize!.Value);
            _current = null;
        }
    }

    /// <summary>Disposes this reader and, only when requested at creation, its source.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _source.Dispose();
        }
    }

    internal void SetSchema(EbmlSchema schema) => _schema = schema;

    internal EbmlElement ReadTree(ref int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++count > _options.MaxElementCount)
        {
            throw new EbmlDataFormatException(ErrorMessages.ElementLimit, ContentFormatErrorCode.LimitExceeded, Current.Offset);
        }

        var header = Current;
        var sourceOffset = _source.CanSeek ? _source.Position : 0;
        EbmlElement element;
        if (header.Type == EbmlElementType.MasterElement)
        {
            element = EbmlElement.Create(header.Identifier, header.Type);
            EnterMaster();
            while (ReadNext())
            {
                element.Children.Add(ReadTree(ref count, cancellationToken));
            }

            LeaveMaster();
        }
        else
        {
            element = EbmlElement.Create(header.Identifier, header.Type, ReadValue());
            if (_source.CanSeek)
            {
                element.RawPayload = BinarySegment.Create(_source, sourceOffset, header.DataSize!.Value);
            }
        }
        element.IsUnknownSize = header.IsUnknownSize;
        element.SizeLength = header.SizeLength;
        element.Offset = header.Offset;
        return element;
    }

    private object ReadValueCore(EbmlElementType type)
    {
        var header = Current;
        if (header.Type == EbmlElementType.MasterElement)
        {
            throw new InvalidOperationException(ErrorMessages.InvalidType);
        }

        if (_schema.Find(header.Identifier) is not null && type != header.Type)
        {
            throw new EbmlDataFormatException(ErrorMessages.InvalidType, ContentFormatErrorCode.InvalidValue, header.Offset);
        }

        var length = header.DataSize!.Value;
        if (!EbmlValueCodec.ValidLength(type, length))
        {
            throw new EbmlDataFormatException(ErrorMessages.InvalidSize, ContentFormatErrorCode.InvalidValue, header.DataOffset);
        }

        object value;
        if (type == EbmlElementType.Binary && _source.CanSeek)
        {
            value = BinarySegment.Create(_source, _source.Position, length);
            SkipData(length);
        }
        else
        {
            if (length > _options.MaxBufferedValueLength)
            {
                throw new EbmlDataFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, header.DataOffset);
            }

            if (type == EbmlElementType.Binary)
            {
                var bytes = new byte[(int)length];
                ReadExactly(bytes);
                value = BinarySegment.Create(bytes);
            }
            else
            {
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)length));
                try
                {
                    var bytes = buffer.AsSpan(0, (int)length);
                    ReadExactly(bytes);
                    value = EbmlValueCodec.Decode(type, bytes, header.DataOffset);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        if (_masters.Count > 0 && _masters[^1].Header.Identifier.EncodedValue == 0x1A45DFA3 && value is ulong number)
        {
            if (header.Identifier.EncodedValue == 0x42F2)
            {
                _headerMaxId = length == 0 ? 4 : number;
            }

            if (header.Identifier.EncodedValue == 0x42F3)
            {
                _headerMaxSize = length == 0 ? 8 : number;
            }
        }
        _current = null;
        return value;
    }

    private bool TryReadFirst()
    {
        try { return _input.TryReadExactly(_headerBytes.AsSpan(0, 1)); }
        catch (ContentFormatException error)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, error.ErrorCode, _input.Position, error);
        }
    }

    private VInt ReadVInt(int offset, int maximum)
    {
        var length = VInt.GetLength(_headerBytes[offset], maximum, _input.Position - 1);
        ReadExactly(_headerBytes.AsSpan(offset + 1, length - 1));
        ulong encoded = 0;
        for (var i = offset; i < offset + length; i++)
        {
            encoded = (encoded << 8) | _headerBytes[i];
        }

        return VInt.FromEncoded(encoded);
    }

    private void ReadExactly(Span<byte> bytes)
    {
        try { _input.ReadContentExactly(bytes); }
        catch (ContentFormatException error)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, error.ErrorCode, _input.Position, error);
        }
    }

    private void SkipData(long count)
    {
        if (_source.CanSeek)
        {
            _source.SkipExactly(count);
            _input.Advance(count);
            return;
        }
        try { _input.SkipExactly(count); }
        catch (ContentFormatException error)
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, error.ErrorCode, _input.Position, error);
        }
    }

    private long? Boundary()
    {
        for (var i = _masters.Count - 1; i >= 0; i--)
        {
            if (_masters[i].Header.DataSize is long length)
            {
                return _masters[i].Header.DataOffset + length;
            }
        }

        return null;
    }

    private sealed class MasterFrame
    {
        internal MasterFrame(EbmlElementHeader header) { Header = header; }
        internal EbmlElementHeader Header { get; }
    }
}
