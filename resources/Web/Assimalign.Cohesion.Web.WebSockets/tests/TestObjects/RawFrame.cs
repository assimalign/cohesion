namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>One frame as the server sent it.</summary>
internal sealed record RawFrame(byte FirstByte, byte SecondByte, byte[] Payload)
{
    public bool IsFinal => (FirstByte & 0x80) != 0;

    public bool IsCompressed => (FirstByte & 0x40) != 0;

    public int Opcode => FirstByte & 0x0F;

    public bool IsMasked => (SecondByte & 0x80) != 0;

    /// <summary>Gets the status code of a close frame.</summary>
    public int CloseCode => (Payload[0] << 8) | Payload[1];
}
