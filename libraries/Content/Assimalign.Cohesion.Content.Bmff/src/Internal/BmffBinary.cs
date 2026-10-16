using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// Adapts legacy field-at-a-time box readers without changing their model or stream lifecycle.
internal static class BmffBinary
{
    internal static BinaryContentReader ReadField(Stream stream, Span<byte> buffer)
    {
        var offset = stream is BmffStream bmff ? bmff.Offset + bmff.Position : stream.Position;
        try
        {
            stream.ReadContentExactly(buffer);
        }
        catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Truncated && stream is BmffStream window)
        {
            throw new ContentFormatException(ErrorMessages.TruncatedHeader, ContentFormatErrorCode.Truncated, window.Offset + window.Position);
        }
        return new BinaryContentReader(buffer, offset);
    }
}
