namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal partial class Http2Frame
{
    /// <summary>
    /// The Prioritized Stream ID carried in the fixed 4-octet prefix of a
    /// <c>PRIORITY_UPDATE</c> frame payload (RFC 9218 §7.1) — the request stream
    /// whose priority the frame re-prioritizes. The remaining payload octets are
    /// the ASCII Priority Field Value and are delivered as the frame payload.
    /// </summary>
    public int PriorityUpdatePrioritizedStreamId { get; set; }
}
