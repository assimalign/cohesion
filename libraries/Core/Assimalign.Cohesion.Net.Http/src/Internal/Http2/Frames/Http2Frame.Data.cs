namespace Assimalign.Cohesion.Net.Http.Internal;

/*
    +---------------+
    |Pad Length? (8)|
    +---------------+-----------------------------------------------+
    |                            Data (*)                         ...
    +---------------------------------------------------------------+
    |                           Padding (*)                       ...
    +---------------------------------------------------------------+
*/
internal partial class Http2Frame
{
    public Http2DataFrameFlags DataFlags
    {
        get => (Http2DataFrameFlags)Flags;
        set => Flags = (byte)value;
    }

    public bool DataEndStream => (DataFlags & Http2DataFrameFlags.EndStream) == Http2DataFrameFlags.EndStream;
    public bool DataHasPadding => (DataFlags & Http2DataFrameFlags.Padded) == Http2DataFrameFlags.Padded;
    public byte DataPadLength { get; set; }
    private int DataPayloadOffset => DataHasPadding ? 1 : 0;
    public int DataPayloadLength => PayloadLength - DataPayloadOffset - DataPadLength;

    public void PrepareData(int streamId, byte? padLength = null)
    {
        PayloadLength = 0;
        Type = Http2FrameType.Data;
        DataFlags = padLength.HasValue ? Http2DataFrameFlags.Padded : Http2DataFrameFlags.None;
        StreamId = streamId;
        DataPadLength = padLength ?? 0;
    }
}