using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Serializes a <see cref="ServerSentEvent"/> to the <c>text/event-stream</c>
/// wire format defined by the WHATWG HTML Server-Sent Events specification.
/// </summary>
/// <remarks>
/// <para>
/// The renderer emits, in order, an optional comment line, the <c>event</c>,
/// <c>id</c>, and <c>retry</c> fields when present, one <c>data:</c> line per line
/// of <see cref="ServerSentEvent.Data"/>, and finally the blank line that
/// dispatches the event. Field values are UTF-8 encoded; the fixed field names and
/// separators are ASCII. There is no reflection and no runtime code generation, so
/// the type is trim- and NativeAOT-safe.
/// </para>
/// </remarks>
public static class ServerSentEventFormatter
{
    private const byte Colon = (byte)':';
    private const byte Space = (byte)' ';
    private const byte NewLine = (byte)'\n';

    /// <summary>
    /// Writes <paramref name="event"/> to <paramref name="writer"/> in the
    /// <c>text/event-stream</c> wire format, terminated by the dispatch blank line.
    /// </summary>
    /// <param name="event">The event to serialize.</param>
    /// <param name="writer">The buffer writer that receives the encoded bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static void Write(ServerSentEvent @event, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (@event.Comment is { } comment)
        {
            // A comment is a line whose field name is empty: ": <text>".
            WriteField(writer, ReadOnlySpan<char>.Empty, comment);
        }

        if (!string.IsNullOrEmpty(@event.EventType))
        {
            WriteField(writer, "event", @event.EventType);
        }

        if (@event.Id is { } id)
        {
            WriteField(writer, "id", id);
        }

        if (@event.Retry is { } retry)
        {
            long milliseconds = (long)retry.TotalMilliseconds;
            if (milliseconds < 0)
            {
                milliseconds = 0;
            }

            WriteField(writer, "retry", milliseconds.ToString(CultureInfo.InvariantCulture));
        }

        if (@event.Data is { } data)
        {
            WriteData(writer, data);
        }

        // The blank line dispatches the event on the client.
        WriteNewLine(writer);
    }

    /// <summary>
    /// Serializes <paramref name="event"/> to a freshly allocated byte array in
    /// the <c>text/event-stream</c> wire format.
    /// </summary>
    /// <param name="event">The event to serialize.</param>
    /// <returns>The UTF-8 encoded event bytes, including the dispatch blank line.</returns>
    public static byte[] Format(ServerSentEvent @event)
    {
        ArrayBufferWriter<byte> writer = new();
        Write(@event, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteData(IBufferWriter<byte> writer, string data)
    {
        // WHATWG SSE — a multi-line data payload is emitted as one "data:" line
        // per source line; the client rejoins them with '\n'. Split on CRLF, CR,
        // or LF so any newline convention round-trips.
        int start = 0;

        while (start <= data.Length)
        {
            int lineEnd = data.Length;
            int nextStart = data.Length + 1; // past-end sentinel — stops the loop when no more breaks

            for (int index = start; index < data.Length; index++)
            {
                char current = data[index];
                if (current == '\n')
                {
                    lineEnd = index;
                    nextStart = index + 1;
                    break;
                }

                if (current == '\r')
                {
                    lineEnd = index;
                    nextStart = index + 1 < data.Length && data[index + 1] == '\n' ? index + 2 : index + 1;
                    break;
                }
            }

            WriteField(writer, "data", data.AsSpan(start, lineEnd - start));
            start = nextStart;
        }
    }

    private static void WriteField(IBufferWriter<byte> writer, ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        if (!name.IsEmpty)
        {
            WriteUtf8(writer, name);
        }

        Span<byte> separator = writer.GetSpan(2);
        separator[0] = Colon;
        separator[1] = Space;
        writer.Advance(2);

        if (!value.IsEmpty)
        {
            WriteUtf8(writer, value);
        }

        WriteNewLine(writer);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> destination = writer.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(value, destination);
        writer.Advance(written);
    }

    private static void WriteNewLine(IBufferWriter<byte> writer)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = NewLine;
        writer.Advance(1);
    }
}
