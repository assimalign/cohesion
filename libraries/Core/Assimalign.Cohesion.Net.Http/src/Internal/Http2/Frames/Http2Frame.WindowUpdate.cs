namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.9
    +-+-------------------------------------------------------------+
    |R|              Window Size Increment (31)                     |
    +-+-------------------------------------------------------------+
*/
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