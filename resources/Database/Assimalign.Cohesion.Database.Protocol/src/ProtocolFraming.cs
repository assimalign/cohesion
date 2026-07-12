using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Creates frame readers and writers over a duplex transport stream.
/// </summary>
public static class ProtocolFraming
{
    /// <summary>
    /// Creates a frame reader over a stream.
    /// </summary>
    /// <param name="stream">The transport stream to read from.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the reader.</param>
    /// <returns>The frame reader.</returns>
    public static IProtocolFrameReader CreateReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ProtocolStreamFrameReader(stream, leaveOpen);
    }

    /// <summary>
    /// Creates a frame writer over a stream.
    /// </summary>
    /// <param name="stream">The transport stream to write to.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the writer.</param>
    /// <returns>The frame writer.</returns>
    public static IProtocolFrameWriter CreateWriter(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ProtocolStreamFrameWriter(stream, leaveOpen);
    }
}
