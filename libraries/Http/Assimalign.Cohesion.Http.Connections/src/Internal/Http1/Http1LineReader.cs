using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>Finds bounded CRLF lines without consuming any following octets.</summary>
internal static class Http1LineReader
{
    internal static LineScan ScanLine(
        in ReadOnlySequence<byte> buffer,
        int maxLineSize,
        out ReadOnlySequence<byte> line,
        out SequencePosition lineEnd,
        bool rejectBareLineBreaks = false)
    {
        long window = Math.Min(buffer.Length, (long)maxLineSize + 2);
        SequenceReader<byte> reader = new(buffer.Slice(0, window));
        if (rejectBareLineBreaks)
        {
            // Search only to the first CR or LF, preserving the byte reader's rejection order:
            // cap + 1 ordinary octets is too long, a bare LF is malformed, and a CR must pair
            // with the very next octet. A CR may be the last octet of a segment or of a read.
            if (reader.TryReadToAny(out line, "\r\n"u8, advancePastDelimiter: false))
            {
                if (line.Length > maxLineSize)
                {
                    lineEnd = buffer.GetPosition((long)maxLineSize + 1);
                    return LineScan.TooLong;
                }
                reader.TryRead(out byte first);
                if (first == (byte)'\n')
                {
                    lineEnd = reader.Position;
                    return LineScan.BareLineFeed;
                }
                if (!reader.TryRead(out byte second))
                {
                    lineEnd = buffer.End;
                    return LineScan.Incomplete;
                }
                lineEnd = reader.Position;
                return second == (byte)'\n' ? LineScan.Complete : LineScan.BareCarriageReturn;
            }
            if (buffer.Length > maxLineSize)
            {
                line = default;
                lineEnd = buffer.GetPosition((long)maxLineSize + 1);
                return LineScan.TooLong;
            }
            line = buffer;
            lineEnd = buffer.End;
            return LineScan.Incomplete;
        }

        // Head syntax is checked by the request-line / field-line parser after CRLF is found.
        if (reader.TryReadTo(out line, "\r\n"u8, advancePastDelimiter: true))
        {
            lineEnd = reader.Position;
            return LineScan.Complete;
        }
        line = default;
        lineEnd = default;
        if (buffer.Length >= (long)maxLineSize + 2)
        {
            return LineScan.TooLong;
        }
        if (buffer.Length == (long)maxLineSize + 1)
        {
            reader.Advance(maxLineSize);
            if (reader.TryPeek(out byte last) && last != (byte)'\r')
            {
                return LineScan.TooLong;
            }
        }
        return LineScan.Incomplete;
    }

    internal enum LineScan
    {
        Incomplete,
        Complete,
        TooLong,
        BareCarriageReturn,
        BareLineFeed,
    }
}
