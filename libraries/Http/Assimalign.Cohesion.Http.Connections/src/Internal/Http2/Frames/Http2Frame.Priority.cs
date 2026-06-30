namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal partial class Http2Frame
{
    public int PriorityStreamDependency { get; set; }

    public bool PriorityIsExclusive { get; set; }

    public byte PriorityWeight { get; set; }

    public void PreparePriority(int streamId, int streamDependency, bool exclusive, byte weight)
    {
        PayloadLength = 5;
        Type = Http2FrameType.Priority;
        StreamId = streamId;
        PriorityStreamDependency = streamDependency;
        PriorityIsExclusive = exclusive;
        PriorityWeight = weight;
    }
}
