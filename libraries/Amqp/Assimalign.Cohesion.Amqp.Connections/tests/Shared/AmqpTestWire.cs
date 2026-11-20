using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Wire bytes the AMQP tests play as the remote peer, and a reader for the frames a connection receives.
/// Each property returns a new array, so a test can never change another test's bytes.
/// </summary>
internal static class AmqpTestWire
{
    /// <summary>
    /// Gets the AMQP 1.0 protocol header (Part 2 §2.2): <c>AMQP 0 1 0 0</c>.
    /// </summary>
    public static byte[] Amqp10Header => new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0 };

    /// <summary>
    /// Gets the SASL 1.0 protocol header (Part 5 §5.3.1): <c>AMQP 3 1 0 0</c>.
    /// </summary>
    public static byte[] Sasl10Header => new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 3, 1, 0, 0 };

    /// <summary>
    /// Gets eight bytes that are not an AMQP protocol header.
    /// </summary>
    public static byte[] NotAProtocolHeader => new byte[] { (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'1', (byte)'.', (byte)'1' };

    /// <summary>
    /// Concatenates byte segments, as a peer that writes them in one flush would deliver them.
    /// </summary>
    public static byte[] Combine(params byte[][] segments)
    {
        int length = 0;

        foreach (byte[] segment in segments)
        {
            length += segment.Length;
        }

        byte[] buffer = new byte[length];
        int offset = 0;

        foreach (byte[] segment in segments)
        {
            Buffer.BlockCopy(segment, 0, buffer, offset, segment.Length);
            offset += segment.Length;
        }

        return buffer;
    }

    /// <summary>
    /// Receives the next frame on an established connection, failing when the peer ends the stream first.
    /// </summary>
    public static async Task<AmqpFrame> ReceiveFrameAsync(IAmqpConnection connection, CancellationToken cancellationToken)
    {
        await foreach (AmqpFrame frame in connection.ReceiveAsync(cancellationToken))
        {
            return frame;
        }

        throw new InvalidOperationException("Expected a frame, but the peer ended the AMQP stream.");
    }
}
