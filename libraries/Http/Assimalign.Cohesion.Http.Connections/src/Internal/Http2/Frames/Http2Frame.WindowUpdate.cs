namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal partial class Http2Frame
{
    public int WindowUpdateSizeIncrement { get; set; }

    public void PrepareWindowUpdate(int streamId, int sizeIncrement)
    {
        PayloadLength = 4;
        Type = Http2FrameType.WindowUpdate;
        Flags = 0;
        StreamId = streamId;
        WindowUpdateSizeIncrement = sizeIncrement;
    }
}
