using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

/// <summary>
/// The wire protocol's diagnostics: a Verbose trace of the frames read from and written to a
/// transport stream, under the <see cref="Keywords.Frames"/> keyword.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Protocol</c>; applications forward it
/// into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>.
/// </para>
/// <para>
/// The public members of <see cref="ProtocolFrameReader"/> and <see cref="ProtocolFrameWriter"/> write
/// the events, and only for the stream reader and writer that their <c>Create</c> factories return: the
/// other readers and writers (the channel's family check, the clients' error translation) decorate a
/// stream one through its public member, so each wire frame is reported once, where it crosses the
/// transport. A frame carries its message type and payload length only, never its payload.
/// </para>
/// <para>
/// No counters: the SQL server writes one frame per result row, and a process-wide count updated per
/// frame by every session would be a contention point (plan D6). Frame failures are not events here:
/// the server session or the client that catches the <see cref="ProtocolException"/> reports it once,
/// with its session or connection.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Protocol")]
internal sealed class ProtocolEventSource : EventSource
{
    public static readonly ProtocolEventSource Log = new();

    private ProtocolEventSource()
    {
    }

    /// <summary>
    /// The keywords that let a tool take one family of Verbose events without the others.
    /// </summary>
    public static class Keywords
    {
        /// <summary>The per-frame trace: <c>FrameRead</c> and <c>FrameWritten</c>.</summary>
        public const EventKeywords Frames = (EventKeywords)0x1;
    }

    /// <summary>
    /// Gets a value indicating whether a listener takes the frame trace.
    /// </summary>
    /// <returns>True when <c>FrameRead</c> and <c>FrameWritten</c> are written.</returns>
    [NonEvent]
    public bool IsFrameTraceEnabled()
        => IsEnabled(EventLevel.Verbose, Keywords.Frames);

    /// <summary>
    /// Writes a frame read from the transport.
    /// </summary>
    /// <param name="frame">The frame the reader returned; null, the clean end of the stream, writes nothing.</param>
    [NonEvent]
    public void FrameRead(ProtocolFrame? frame)
    {
        if (frame is { } value && IsEnabled(EventLevel.Verbose, Keywords.Frames))
        {
            FrameRead(value.Type.ToString(), value.Payload.Length);
        }
    }

    /// <summary>
    /// Writes a frame written to the transport.
    /// </summary>
    /// <param name="type">The frame's message type.</param>
    /// <param name="payloadLength">The frame's payload length in bytes.</param>
    [NonEvent]
    public void FrameWritten(ProtocolMessageType type, int payloadLength)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Frames))
        {
            FrameWritten(type.ToString(), payloadLength);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Frames, Message = "Read a {0} frame with a {1}-byte payload.")]
    private void FrameRead(string messageType, int payloadLength)
        => WriteEvent(1, messageType, payloadLength);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Frames, Message = "Wrote a {0} frame with a {1}-byte payload.")]
    private void FrameWritten(string messageType, int payloadLength)
        => WriteEvent(2, messageType, payloadLength);
}
