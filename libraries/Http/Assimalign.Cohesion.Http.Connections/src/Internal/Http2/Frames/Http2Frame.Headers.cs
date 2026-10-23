namespace Assimalign.Cohesion.Http.Connections.Internal;

[global::System.Diagnostics.DebuggerDisplay("Http2Frame")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal partial class Http2Frame
{
    public Http2HeadersFrameFlags HeadersFlags
    {
        get => (Http2HeadersFrameFlags)Flags;
        set => Flags = (byte)value;
    }

    public bool HeadersEndHeaders => (HeadersFlags & Http2HeadersFrameFlags.EndHeaders) == Http2HeadersFrameFlags.EndHeaders;

    public bool HeadersEndStream => (HeadersFlags & Http2HeadersFrameFlags.EndStream) == Http2HeadersFrameFlags.EndStream;

    public bool HeadersHasPadding => (HeadersFlags & Http2HeadersFrameFlags.Padded) == Http2HeadersFrameFlags.Padded;

    public bool HeadersHasPriority => (HeadersFlags & Http2HeadersFrameFlags.Priority) == Http2HeadersFrameFlags.Priority;

    public byte HeadersPadLength { get; set; }

    public int HeadersStreamDependency { get; set; }

    public byte HeadersPriorityWeight { get; set; }

    private int HeadersPayloadOffset => (HeadersHasPadding ? 1 : 0) + (HeadersHasPriority ? 5 : 0);

    public int HeadersPayloadLength => PayloadLength - HeadersPayloadOffset - HeadersPadLength;

    public void PrepareHeaders(Http2HeadersFrameFlags flags, int streamId)
    {
        PayloadLength = 0;
        Type = Http2FrameType.Headers;
        HeadersFlags = flags;
        StreamId = streamId;
    }

    private sealed class DebugView
    {
        private readonly Http2Frame _target;

        public DebugView(Http2Frame value)
        {
            _target = value;
        }

        public int PayloadLength => _target.PayloadLength;
        public global::Assimalign.Cohesion.Http.Connections.Internal.Http2FrameType Type => _target.Type;
        public byte Flags => _target.Flags;
        public int StreamId => _target.StreamId;
        public global::Assimalign.Cohesion.Http.Connections.Internal.Http2DataFrameFlags DataFlags => _target.DataFlags;
        public bool DataEndStream => _target.DataEndStream;
        public bool DataHasPadding => _target.DataHasPadding;
        public byte DataPadLength => _target.DataPadLength;
        public int DataPayloadLength => _target.DataPayloadLength;
        public int GoAwayLastStreamId => _target.GoAwayLastStreamId;
        public global::Assimalign.Cohesion.Http.Connections.Internal.Http2ErrorCode GoAwayErrorCode => _target.GoAwayErrorCode;
        public int WindowUpdateSizeIncrement => _target.WindowUpdateSizeIncrement;
        public global::Assimalign.Cohesion.Http.Connections.Internal.Http2SettingsFrameFlags SettingsFlags => _target.SettingsFlags;
        public bool SettingsAck => _target.SettingsAck;
        public global::Assimalign.Cohesion.Http.Connections.Internal.Http2ErrorCode RstStreamErrorCode => _target.RstStreamErrorCode;
        public int PriorityUpdatePrioritizedStreamId => _target.PriorityUpdatePrioritizedStreamId;
        public global::Assimalign.Cohesion.Http.Connections.Internal.Http2PingFrameFlags PingFlags => _target.PingFlags;
        public bool PingAck => _target.PingAck;
        public Http2HeadersFrameFlags HeadersFlags => _target.HeadersFlags;
        public bool HeadersEndHeaders => _target.HeadersEndHeaders;
        public bool HeadersEndStream => _target.HeadersEndStream;
        public bool HeadersHasPadding => _target.HeadersHasPadding;
        public bool HeadersHasPriority => _target.HeadersHasPriority;
        public byte HeadersPadLength => _target.HeadersPadLength;
        public int HeadersStreamDependency => _target.HeadersStreamDependency;
        public byte HeadersPriorityWeight => _target.HeadersPriorityWeight;
        public int HeadersPayloadLength => _target.HeadersPayloadLength;
    }
}
