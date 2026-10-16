using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>Writes typed EBML elements with precomputed, streaming, or back-patched master sizes.</summary>
/// <remarks>Borrowed outputs are never closed by default. Known master sizes work without seeking.
/// A failed write can leave partial output; use a separate destination when replacing a document.</remarks>
public sealed partial class EbmlWriter : IDisposable
{
    private readonly Stream _output;
    private readonly EbmlSchema _schema;
    private readonly bool _leaveOpen;
    private readonly int _maxDepth;
    private readonly List<MasterFrame> _masters = new();
    private long _position;
    private bool _disposed;
    private int _baseDepth;
    internal bool RecalculateCrc { get; set; }
    private Dictionary<EbmlElement, uint> _crcCache = new();

    private EbmlWriter(Stream output, EbmlSchema schema, bool leaveOpen, int maxDepth)
    {
        _output = output;
        _schema = schema;
        _leaveOpen = leaveOpen;
        _maxDepth = maxDepth;
        _position = output.CanSeek ? output.Position : 0;
    }

    /// <summary>Creates a writer over a writable output.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="schema">The schema used for master eligibility; null selects the RFC header schema.</param>
    /// <param name="leaveOpen">Whether disposal leaves the destination open.</param>
    /// <param name="maxDepth">The maximum master depth, from one through 256.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="ArgumentNullException">The output is null.</exception>
    /// <exception cref="ArgumentException">The output is not writable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth is invalid.</exception>
    public static EbmlWriter Create(Stream output, EbmlSchema? schema = null, bool leaveOpen = true, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException(ErrorMessages.OutputWritable, nameof(output));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, 256);
        return new EbmlWriter(output, schema ?? EbmlSchema.Header, leaveOpen, maxDepth);
    }

    /// <summary>Gets the output byte position (relative to creation on non-seekable outputs).</summary>
    public long Position => _position;

    /// <summary>Writes an unsigned integer using the minimum payload width unless specified.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="value">The unsigned value.</param>
    /// <param name="length">Zero through eight, or -1 for minimal width.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID, value, or width is invalid.</exception>
    public void WriteUnsignedInteger(VInt identifier, ulong value, int length = -1, int sizeLength = 0) =>
        WriteValue(identifier, EbmlElementType.UnsignedInteger, value, length, sizeLength);

    /// <summary>Writes a signed integer using minimal two's-complement encoding unless specified.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="value">The signed value.</param>
    /// <param name="length">Zero through eight, or -1 for minimal width.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID, value, or width is invalid.</exception>
    public void WriteSignedInteger(VInt identifier, long value, int length = -1, int sizeLength = 0) =>
        WriteValue(identifier, EbmlElementType.SignedInteger, value, length, sizeLength);

    /// <summary>Writes a float at the smallest lossless width unless specified.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="value">The floating value.</param>
    /// <param name="length">Zero, four, eight, or -1 for minimal lossless width.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID, value, or width is invalid.</exception>
    public void WriteFloat(VInt identifier, double value, int length = -1, int sizeLength = 0) =>
        WriteValue(identifier, EbmlElementType.Float, value, length, sizeLength);

    /// <summary>Writes printable ASCII and optional NUL padding.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="value">The string.</param>
    /// <param name="length">The padded byte length, or -1 for minimal width.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID, characters, or width is invalid.</exception>
    public void WriteAsciiString(VInt identifier, string value, int length = -1, int sizeLength = 0) =>
        WriteValue(identifier, EbmlElementType.AsciiString, value, length, sizeLength);

    /// <summary>Writes strict UTF-8 and optional NUL padding.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="value">The string.</param>
    /// <param name="length">The padded byte length, or -1 for minimal width.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID, encoding, or width is invalid.</exception>
    public void WriteUtf8String(VInt identifier, string value, int length = -1, int sizeLength = 0) =>
        WriteValue(identifier, EbmlElementType.Utf8String, value, length, sizeLength);

    /// <summary>Writes exact signed nanoseconds since 2001-01-01T00:00:00 UTC.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="nanoseconds">The exact timestamp.</param>
    /// <param name="length">Zero, eight, or -1 for minimal width.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID, value, or width is invalid.</exception>
    public void WriteDate(VInt identifier, long nanoseconds, int length = -1, int sizeLength = 0) =>
        WriteValue(identifier, EbmlElementType.Date, nanoseconds, length, sizeLength);

    /// <summary>Copies a binary payload without materializing lazy ranges.</summary>
    /// <param name="identifier">The encoded element ID.</param>
    /// <param name="value">The borrowed binary payload.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentException">The ID or width is invalid.</exception>
    /// <exception cref="ContentFormatException">The lazy source is truncated.</exception>
    public void WriteBinary(VInt identifier, BinarySegment value, int sizeLength = 0)
    {
        ValidateLeaf(identifier, EbmlElementType.Binary, value, value.Length);
        WriteHeader(identifier, value.Length, sizeLength);
        EnsureCount(value.Length);
        value.CopyTo(_output);
        _position += value.Length;
    }

    /// <summary>Writes a Void element with the requested number of zero payload bytes.</summary>
    /// <param name="length">The nonnegative payload length, excluding the header.</param>
    /// <param name="sizeLength">The size VINT width, or zero for minimal width.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative.</exception>
    /// <exception cref="ArgumentException">The width is invalid.</exception>
    public void WriteVoid(long length, int sizeLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        WriteHeader(VInt.FromEncoded(0xEC), length, sizeLength);
        Span<byte> zeros = stackalloc byte[1024];
        zeros.Clear();
        while (length > 0)
        {
            var count = (int)Math.Min(length, zeros.Length);
            WriteBytes(zeros[..count]);
            length -= count;
        }
    }

    /// <summary>Starts a known-size, unknown-size, or back-patched master.</summary>
    /// <param name="identifier">The master ID.</param>
    /// <param name="size">A precomputed payload size; null requests unknown size or back-patching.</param>
    /// <param name="sizeLength">The size VINT width; zero uses minimal known width or eight for unknown size.</param>
    /// <param name="backPatch">Whether to fill the size when EndMaster is called; requires seekable output and null size.</param>
    /// <exception cref="ArgumentException">The ID, size, schema eligibility, or mode is invalid.</exception>
    /// <exception cref="InvalidOperationException">The output cannot seek or nesting exceeds its limit.</exception>
    public void BeginMaster(VInt identifier, long? size = null, int sizeLength = 0, bool backPatch = false)
    {
        if (_baseDepth + _masters.Count >= _maxDepth)
        {
            throw new InvalidOperationException(ErrorMessages.DepthLimit);
        }

        var definition = _schema.Find(identifier);
        if (definition is not null && definition.Type != EbmlElementType.MasterElement)
        {
            throw new ArgumentException(ErrorMessages.InvalidType, nameof(identifier));
        }

        if (backPatch && (!_output.CanSeek || size is not null))
        {
            throw new InvalidOperationException(ErrorMessages.OutputSeekable);
        }

        if (size is null && !backPatch && definition?.UnknownSizeAllowed != true)
        {
            throw new ArgumentException(ErrorMessages.UnknownSize, nameof(identifier));
        }

        var width = sizeLength == 0 && size is null ? 8 : sizeLength;
        var sizePosition = _position + identifier.Length;
        WriteHeader(identifier, size, width);
        _masters.Add(new MasterFrame(_position, size, sizePosition, width, backPatch));
    }

    /// <summary>Completes a master, checking or back-patching its size.</summary>
    /// <exception cref="InvalidOperationException">No master is open or its actual size differs from the declared size.</exception>
    /// <exception cref="ArgumentException">The reserved size width cannot hold the result.</exception>
    public void EndMaster()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_masters.Count == 0)
        {
            throw new InvalidOperationException(ErrorMessages.WriterState);
        }

        var frame = _masters[^1];
        var length = _position - frame.DataOffset;
        if (frame.Size is long size && size != length)
        {
            throw new InvalidOperationException(ErrorMessages.WriterState);
        }

        if (frame.BackPatch)
        {
            var encoded = VInt.EncodeSize((ulong)length, frame.SizeLength);
            var end = _output.Position;
            try
            {
                _output.Position = frame.SizePosition;
                encoded.Write(_output);
            }
            finally { _output.Position = end; }
        }
        _masters.RemoveAt(_masters.Count - 1);
    }

    /// <summary>Writes an editable tree, precomputing known master sizes without buffering payloads.</summary>
    /// <param name="element">The tree to write.</param>
    /// <exception cref="ArgumentNullException">The element is null.</exception>
    /// <exception cref="ArgumentException">An ID, value, or explicit width is invalid.</exception>
    /// <exception cref="InvalidOperationException">The depth limit or master boundary is violated.</exception>
    /// <exception cref="ContentFormatException">A lazy source is truncated.</exception>
    public void WriteElement(EbmlElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_baseDepth == 0 && _masters.Count == 0)
        {
            _crcCache.Clear();
        }

        var length = DataLength(element, _baseDepth + _masters.Count);
        if (element.Type == EbmlElementType.MasterElement)
        {
            BeginMaster(element.Identifier, element.IsUnknownSize ? null : length, SizeWidth(element, length));
            var first = 0;
            if (RecalculateCrc && HasCrc(element))
            {
                if (!_crcCache.TryGetValue(element, out var crc))
                {
                    using var checksum = new EbmlCrcStream();
                    using var calculator = new EbmlWriter(checksum, _schema, true, _maxDepth)
                    { RecalculateCrc = true, _baseDepth = _baseDepth + _masters.Count, _crcCache = _crcCache };
                    for (var i = 1; i < element.Children.Count; i++)
                    {
                        calculator.WriteElement(element.Children[i]);
                    }

                    crc = checksum.Value;
                    _crcCache.Add(element, crc);
                }

                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, crc);
                WriteBinary(element.Children[0].Identifier, BinarySegment.Create(bytes), element.Children[0].SizeLength);
                first = 1;
            }
            for (var i = first; i < element.Children.Count; i++)
            {
                WriteElement(element.Children[i]);
            }

            EndMaster();
        }
        else if (element.RawPayload is BinarySegment raw)
        {
            WriteHeader(element.Identifier, raw.Length, SizeWidth(element, raw.Length));
            raw.CopyTo(_output);
            _position += raw.Length;
        }
        else if (element.Value is BinarySegment binary)
        {
            WriteBinary(element.Identifier, binary, SizeWidth(element, binary.Length));
        }
        else
        {
            WriteValue(element.Identifier, element.Type, element.Value!, -1, SizeWidth(element, length));
        }
    }

    /// <summary>Disposes the writer and optionally the destination. Open masters are not implicitly completed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _output.Dispose();
        }
    }

    internal long DataLength(EbmlElement element, int depth)
    {
        if (element.Type != EbmlElementType.MasterElement)
        {
            return element.RawPayload?.Length ?? (element.Value is BinarySegment segment ? segment.Length :
                EbmlValueCodec.GetWireLength(_schema.Find(element.Identifier), element.Type, element.Value!));
        }

        if (depth >= _maxDepth)
        {
            throw new InvalidOperationException(ErrorMessages.DepthLimit);
        }

        long total = 0;
        foreach (var child in element.Children)
        {
            var size = DataLength(child, depth + 1);
            var width = SizeWidth(child, size);
            var next = size + child.Identifier.Length + width;
            if (next > long.MaxValue - total)
            {
                throw new ArgumentException(ErrorMessages.InvalidSize);
            }

            total += next;
        }
        return total;
    }

    private static int SizeWidth(EbmlElement element, long size)
    {
        if (element.IsUnknownSize)
        {
            return element.SizeLength == 0 ? 8 : element.SizeLength;
        }

        return Math.Max(element.SizeLength, VInt.EncodeSize((ulong)size).Length);
    }

    private static bool HasCrc(EbmlElement element) => element.Children.Count > 0 &&
        element.Children[0].Identifier.EncodedValue == 0xBF &&
        element.Children[0].Value is BinarySegment segment && segment.Length == 4;

    private void WriteValue(VInt identifier, EbmlElementType type, object value, int length, int sizeLength)
    {
        var count = EbmlValueCodec.GetWireLength(_schema.Find(identifier), type, value, length);
        ValidateLeaf(identifier, type, value, count);
        WriteHeader(identifier, count, sizeLength);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, count));
        try
        {
            var bytes = buffer.AsSpan(0, count);
            EbmlValueCodec.Encode(type, value, bytes);
            WriteBytes(bytes);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void WriteHeader(VInt identifier, long? size, int sizeLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!identifier.IsValidIdentifier)
        {
            throw new ArgumentException(ErrorMessages.InvalidVInt, nameof(identifier));
        }

        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var encodedSize = size is long length ? VInt.EncodeSize((ulong)length, sizeLength) : VInt.UnknownSize(sizeLength);
        EnsureCount(identifier.Length + encodedSize.Length + (size ?? 0));
        identifier.Write(_output);
        encodedSize.Write(_output);
        _position += identifier.Length + encodedSize.Length;
    }

    private void ValidateLeaf(VInt identifier, EbmlElementType type, object value, long length)
    {
        var definition = _schema.Find(identifier);
        if (definition is not null && (definition.Type != type || !definition.Contains(value) ||
            length < definition.MinimumLength || length > definition.MaximumLength))
        {
            throw new EbmlDataFormatException(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidValue, _position);
        }
    }

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCount(bytes.Length);
        _output.Write(bytes);
        _position += bytes.Length;
    }

    private void EnsureCount(long count)
    {
        if (count > long.MaxValue - _position)
        {
            throw new InvalidOperationException(ErrorMessages.WriterState);
        }

        foreach (var master in _masters)
        {
            if (master.Size is long size && count > size - (_position - master.DataOffset))
            {
                throw new InvalidOperationException(ErrorMessages.WriterState);
            }
        }
    }

    private sealed class MasterFrame
    {
        internal MasterFrame(long dataOffset, long? size, long sizePosition, int sizeLength, bool backPatch)
        {
            DataOffset = dataOffset; Size = size; SizePosition = sizePosition; SizeLength = sizeLength; BackPatch = backPatch;
        }
        internal long DataOffset { get; }
        internal long? Size { get; }
        internal long SizePosition { get; }
        internal int SizeLength { get; }
        internal bool BackPatch { get; }
    }
}
