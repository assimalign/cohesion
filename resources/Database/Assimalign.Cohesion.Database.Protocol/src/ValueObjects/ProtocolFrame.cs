using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// One complete protocol frame: a message type and its payload.
/// </summary>
/// <param name="Type">The message type of the frame.</param>
/// <param name="Payload">The frame payload; empty for payload-less messages such as ping.</param>
public readonly record struct ProtocolFrame(ProtocolMessageType Type, ReadOnlyMemory<byte> Payload)
{
    /// <summary>
    /// Gets the header describing this frame.
    /// </summary>
    public ProtocolFrameHeader Header => new(Type, (uint)Payload.Length);
}
