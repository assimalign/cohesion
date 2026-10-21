using System.Diagnostics.Tracing;
using System.Threading;

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
/// The stream reader and writer that the <see cref="ProtocolFrameReader"/> and
/// <see cref="ProtocolFrameWriter"/> <c>Create</c> factories return write the events from their own
/// cores, once a frame has crossed the transport. Every other reader and writer (the channel's family
/// check, the clients' error translation) decorates a stream one through its public member, so each
/// wire frame is reported once, and the public bases carry no tracing and no type check. A frame
/// carries its message type and payload length only, never its payload.
/// </para>
/// <para>
/// Plan D6: ordinary writers (including SQL's per-row writer) never update process-wide counters
/// per frame; doing so would create contention between sessions. Exact attempted writes and explicit
/// flushes count batched writers only. Pool counters cover their buffers and borrowed reads only,
/// independently of listeners. Frame failures are reported by their owning session or client.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Protocol")]
internal sealed class ProtocolEventSource : EventSource
{
    public static readonly ProtocolEventSource Log = new();

    private long _transportWrites;
    private long _transportFlushes;
    private long _rentedBuffers;
    private long _returnedBuffers;
    private long _pooledBuffersInUse;
    private PollingCounter? _writesCounter;
    private PollingCounter? _flushesCounter;
    private PollingCounter? _buffersCounter;

    internal long TransportWrites => Volatile.Read(ref _transportWrites);
    internal long TransportFlushes => Volatile.Read(ref _transportFlushes);
    internal long RentedBuffers => Volatile.Read(ref _rentedBuffers);
    internal long ReturnedBuffers => Volatile.Read(ref _returnedBuffers);
    internal long PooledBuffersInUse => Volatile.Read(ref _pooledBuffersInUse);

    [NonEvent]
    internal void TransportWrite() => Interlocked.Increment(ref _transportWrites);
    [NonEvent]
    internal void TransportFlush() => Interlocked.Increment(ref _transportFlushes);
    [NonEvent]
    internal void BufferRented()
    {
        Interlocked.Increment(ref _rentedBuffers);
        Interlocked.Increment(ref _pooledBuffersInUse);
    }
    [NonEvent]
    internal void BufferReturned()
    {
        Interlocked.Decrement(ref _pooledBuffersInUse);
        Interlocked.Increment(ref _returnedBuffers);
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable) { return; }
        _writesCounter ??= new PollingCounter("transport-write-calls", this, () => TransportWrites)
        { DisplayName = "Batched Writer Transport Write Calls" };
        _flushesCounter ??= new PollingCounter("transport-flush-calls", this, () => TransportFlushes)
        { DisplayName = "Batched Writer Explicit Transport Flush Calls" };
        _buffersCounter ??= new PollingCounter("pooled-buffers-in-use", this, () => PooledBuffersInUse)
        { DisplayName = "Batched Writer and Borrowed Read Buffers In Use" };
    }

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
    /// Writes a frame read from the transport. The clean end of the stream is not a frame and is
    /// not written.
    /// </summary>
    /// <param name="type">The frame's message type.</param>
    /// <param name="payloadLength">The frame's payload length in bytes.</param>
    [NonEvent]
    public void FrameRead(ProtocolMessageType type, int payloadLength)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Frames))
        {
            FrameRead(type.ToString(), payloadLength);
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
