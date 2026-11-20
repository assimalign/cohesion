using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A frame a scripted peer read from the engine under test.
/// </summary>
internal sealed class AmqpTestFrame
{
    private AmqpTestFrame(AmqpFrameHeader header, AmqpPerformative? performative, byte[] payload)
    {
        Header = header;
        Performative = performative;
        Payload = payload;
    }

    public AmqpFrameHeader Header { get; }

    /// <summary>
    /// Gets the frame's performative, or <see langword="null"/> for an empty frame.
    /// </summary>
    public AmqpPerformative? Performative { get; }

    /// <summary>
    /// Gets the bytes that followed the performative: the payload of a <c>transfer</c>.
    /// </summary>
    public byte[] Payload { get; }

    public static AmqpTestFrame Decode(AmqpFrameHeader header, byte[] body)
    {
        if (body.Length == 0)
        {
            return new AmqpTestFrame(header, null, []);
        }

        AmqpReader reader = new(body);
        AmqpPerformative performative = AmqpPerformative.Decode(ref reader);

        return new AmqpTestFrame(header, performative, reader.RemainingSpan.ToArray());
    }
}
