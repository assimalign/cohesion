using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The validated wire header of an ISO/IEC 14496-12 box.</summary>
// Deviates from the generated value-object rule per the Stage 2 brief: this is a binary header.
public readonly struct BmffBoxHeader
{
    private BmffBoxHeader(BmffBoxType type, long? size, int headerSize, long offset, Guid? extendedType, bool extendsToEnd)
    {
        BoxType = type;
        TotalSize = size;
        HeaderSize = headerSize;
        Offset = offset;
        ExtendedType = extendedType;
        ExtendsToEnd = extendsToEnd;
    }

    /// <summary>Gets the raw FourCC, including unregistered values.</summary>
    public BmffBoxType BoxType { get; }
    /// <summary>Gets the total byte count, or null for size zero on an input of unknown length.</summary>
    public long? TotalSize { get; }
    /// <summary>Gets the header byte count: 8, 16, 24 or 32.</summary>
    public int HeaderSize { get; }
    /// <summary>Gets the absolute position of the size field.</summary>
    public long Offset { get; }
    /// <summary>Gets the UUID interpreted in network byte order.</summary>
    public Guid? ExtendedType { get; }
    /// <summary>Gets whether the wire size was zero (the final top-level box).</summary>
    public bool ExtendsToEnd { get; }

    /// <summary>Computes the smallest header capable of describing a payload.</summary>
    /// <param name="boxType">The raw type.</param>
    /// <param name="payloadSize">The nonnegative payload byte count.</param>
    /// <param name="offset">The absolute start.</param>
    /// <param name="extendedType">Required exactly for uuid boxes.</param>
    /// <returns>The computed header.</returns>
    /// <exception cref="ArgumentException">The extended type does not match the box type.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range cannot be represented.</exception>
    public static BmffBoxHeader Create(BmffBoxType boxType, long payloadSize, long offset = 0, Guid? extendedType = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadSize);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if ((boxType == BmffBoxType.uuid) != extendedType.HasValue)
        {
            throw new ArgumentException(ErrorMessages.ExtendedTypeRequired, nameof(extendedType));
        }
        var headerSize = extendedType.HasValue ? 24 : 8;
        if (payloadSize > uint.MaxValue - headerSize)
        {
            headerSize += 8;
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, long.MaxValue - headerSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadSize, long.MaxValue - headerSize - offset);
        return new BmffBoxHeader(boxType, payloadSize + headerSize, headerSize, offset, extendedType, false);
    }

    /// <summary>Reads a header without reading or allocating its payload.</summary>
    /// <param name="stream">The borrowed source at the header.</param>
    /// <param name="offset">Its absolute position.</param>
    /// <param name="containerEnd">The absolute enclosing end, if known.</param>
    /// <param name="isTopLevel">Whether a zero size is legal here.</param>
    /// <param name="header">The parsed header, or default at clean end.</param>
    /// <returns>False only at a clean container or source end.</returns>
    /// <exception cref="ContentFormatException">The header is truncated, invalid, or exceeds the supported range.</exception>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The supplied bounds are invalid.</exception>
    public static bool TryRead(Stream stream, long offset, long? containerEnd, bool isTopLevel, out BmffBoxHeader header)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (containerEnd.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(containerEnd.Value, offset);
        }
        header = default;
        if (containerEnd == offset)
        {
            return false;
        }
        // A temporary bounded cursor lets the shared exact-read primitive report partial delivery.
        using var window = ContentSliceStream.CreateForwardOnly(stream,
            Math.Min(32, containerEnd.HasValue ? containerEnd.Value - offset : long.MaxValue - offset));
        Span<byte> bytes = stackalloc byte[16];
        try
        {
            if (!window.TryReadExactly(bytes[..8]))
            {
                if (containerEnd.HasValue)
                {
                    throw new ContentFormatException(ErrorMessages.TruncatedHeader, ContentFormatErrorCode.Truncated, offset);
                }
                return false;
            }
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var type = (BmffBoxType)BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
            ulong size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                window.ReadContentExactly(bytes[..8]);
                size = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                headerSize += 8;
            }
            var requiredHeaderSize = headerSize + (type == BmffBoxType.uuid ? 16 : 0);
            if (size32 != 0 && size < (ulong)requiredHeaderSize)
            {
                throw new ContentFormatException(ErrorMessages.InvalidBoxSize, ContentFormatErrorCode.InvalidStructure, offset);
            }
            Guid? extendedType = null;
            if (type == BmffBoxType.uuid)
            {
                window.ReadContentExactly(bytes);
                extendedType = new Guid(bytes, bigEndian: true);
                headerSize += 16;
            }
            if (size32 == 0 && !isTopLevel)
            {
                throw new ContentFormatException(ErrorMessages.NestedZeroSize, ContentFormatErrorCode.InvalidStructure, offset);
            }
            long? total = size32 == 0 ? containerEnd - offset : size <= (ulong)(long.MaxValue - offset) ? (long)size : null;
            if (size32 != 0 && !total.HasValue)
            {
                throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, offset);
            }
            if (total < headerSize || (containerEnd.HasValue && total > containerEnd - offset))
            {
                throw new ContentFormatException(ErrorMessages.InvalidBoxSize, ContentFormatErrorCode.InvalidStructure, offset);
            }
            header = new BmffBoxHeader(type, total, headerSize, offset, extendedType, size32 == 0);
            return true;
        }
        catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Truncated)
        {
            throw new ContentFormatException(ErrorMessages.TruncatedHeader, ContentFormatErrorCode.Truncated, offset + window.Position);
        }
    }

    /// <summary>Writes this header, retaining its original size encoding.</summary>
    /// <param name="stream">The borrowed output.</param>
    /// <exception cref="ArgumentNullException">The output is null.</exception>
    /// <exception cref="InvalidOperationException">The header is uninitialized.</exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (HeaderSize == 0)
        {
            throw new InvalidOperationException(ErrorMessages.InvalidBoxSize);
        }
        Span<byte> bytes = stackalloc byte[32];
        var large = HeaderSize == (ExtendedType.HasValue ? 32 : 16);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ExtendsToEnd ? 0u : large ? 1u : (uint)TotalSize!.Value);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], (uint)BoxType);
        var position = 8;
        if (large)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (ulong)TotalSize!.Value);
            position += 8;
        }
        if (ExtendedType.HasValue)
        {
            ExtendedType.Value.TryWriteBytes(bytes[position..], bigEndian: true, out _);
        }
        stream.Write(bytes[..HeaderSize]);
    }
}
