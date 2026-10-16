using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Internal;

internal static class HeifFields
{
    internal static uint Read(BmffStream stream, int width)
    {
        Span<byte> bytes = stackalloc byte[4];
        var start = stream.Offset + stream.Position;
        try { stream.ReadContentExactly(bytes[..width]); }
        catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Truncated)
        { throw new ContentFormatException(ErrorMessages.Truncated, ContentFormatErrorCode.Truncated, start); }
        var reader = new BinaryContentReader(bytes[..width], start);
        return width == 1 ? reader.ReadByte() : width == 2 ? reader.ReadUInt16BigEndian() : reader.ReadUInt32BigEndian();
    }
    internal static void Write(BmffStream stream, uint value, int width)
    {
        if ((width == 1 && value > byte.MaxValue) || (width == 2 && value > ushort.MaxValue))
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, stream.Offset + stream.Position);
        }

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes[(4-width)..]);
    }
    internal static byte[] Bytes(BmffStream stream, long length, int limit)
    {
        if (length > limit || length > Array.MaxLength)
        {
            throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, stream.Offset + stream.Position);
        }

        var result = new byte[(int)length];
        var start = stream.Offset + stream.Position;
        try { stream.ReadContentExactly(result); }
        catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Truncated)
        { throw new ContentFormatException(ErrorMessages.Truncated, ContentFormatErrorCode.Truncated, start); }
        return result;
    }
    internal static int Count(uint count, int maximum, long remaining, int minimumBytes, long position)
    {
        if (count > maximum)
        {
            throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, position);
        }

        if ((long)count * minimumBytes > remaining)
        {
            throw new ContentFormatException(ErrorMessages.Truncated, ContentFormatErrorCode.Truncated, position);
        }

        return (int)count;
    }
    internal static void Version(byte version, uint flags, byte maximumVersion, uint allowedFlags, long position)
    {
        if (version > maximumVersion || (flags & ~allowedFlags) != 0)
        {
            throw new ContentFormatException(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, position);
        }
    }
    internal static BinarySegment Segment(BmffStream stream)
    {
        // BMFF has no public lazy-range accessor. Its media node supplies the same borrowed range.
        var media = new MediaDataBox(0, 0);
        media.Read(stream);
        var result = media.Payload.Slice(stream.Position, stream.Remaining);
        stream.SkipExactly(stream.Remaining);
        return result;
    }
}
