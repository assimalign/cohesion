namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

/// <summary>
/// Snapshot of an HTTP/2 endpoint's negotiated SETTINGS parameters
/// (RFC 9113 §6.5.2 + RFC 8441 §3).
/// </summary>
/// <remarks>
/// <para>
/// Two instances live on every connection: one tracks the parameters
/// <em>we</em> advertised to the peer (the server's local settings), the
/// other tracks the parameters the peer advertised to <em>us</em> (the
/// client's remote settings). The remote settings govern how we write
/// outbound frames (max frame size, max header list size); the local
/// settings govern how we read inbound frames (the maximum we will
/// accept).
/// </para>
/// <para>
/// Unknown setting identifiers MUST be ignored per RFC 9113 §6.5.2 so
/// new parameters defined by future extensions do not cause connection
/// failure. <see cref="TryApply"/> implements that rule along with the
/// per-parameter range checks defined by the spec.
/// </para>
/// </remarks>
internal sealed class Http2ConnectionSettings
{
    /// <summary>Default HEADER_TABLE_SIZE (RFC 9113 §6.5.2).</summary>
    public const uint InitialHeaderTableSize = 4096;

    /// <summary>Default ENABLE_PUSH (RFC 9113 §6.5.2). 1 = enabled.</summary>
    public const uint InitialEnablePush = 1;

    /// <summary>
    /// Default MAX_CONCURRENT_STREAMS (RFC 9113 §6.5.2). The spec uses
    /// "no limit" as the default; <see cref="uint.MaxValue"/> is the
    /// sentinel.
    /// </summary>
    public const uint InitialMaxConcurrentStreams = uint.MaxValue;

    /// <summary>Default INITIAL_WINDOW_SIZE (RFC 9113 §6.5.2).</summary>
    public const uint InitialInitialWindowSize = 65535;

    /// <summary>
    /// Default MAX_FRAME_SIZE (RFC 9113 §6.5.2). Equal to the minimum
    /// the spec allows endpoints to advertise.
    /// </summary>
    public const uint InitialMaxFrameSize = 16_384;

    /// <summary>
    /// Default MAX_HEADER_LIST_SIZE (RFC 9113 §6.5.2). The spec uses
    /// "unlimited" as the default; <see cref="uint.MaxValue"/> is the
    /// sentinel.
    /// </summary>
    public const uint InitialMaxHeaderListSize = uint.MaxValue;

    /// <summary>Default ENABLE_CONNECT_PROTOCOL (RFC 8441 §3). 0 = disabled.</summary>
    public const uint InitialEnableConnectProtocol = 0;

    /// <summary>Minimum legal MAX_FRAME_SIZE (RFC 9113 §6.5.2). 2^14.</summary>
    public const uint MinMaxFrameSize = 16_384;

    /// <summary>Maximum legal MAX_FRAME_SIZE (RFC 9113 §6.5.2). 2^24 - 1.</summary>
    public const uint MaxMaxFrameSize = 16_777_215;

    /// <summary>Maximum legal INITIAL_WINDOW_SIZE (RFC 9113 §6.5.2). 2^31 - 1.</summary>
    public const uint MaxInitialWindowSize = int.MaxValue;

    /// <summary>HEADER_TABLE_SIZE — HPACK dynamic table cap advertised to the peer.</summary>
    public uint HeaderTableSize { get; set; } = InitialHeaderTableSize;

    /// <summary>ENABLE_PUSH — server push toggle. Must be 0 or 1.</summary>
    public uint EnablePush { get; set; } = InitialEnablePush;

    /// <summary>MAX_CONCURRENT_STREAMS — peer's max concurrent stream cap.</summary>
    public uint MaxConcurrentStreams { get; set; } = InitialMaxConcurrentStreams;

    /// <summary>
    /// INITIAL_WINDOW_SIZE — initial flow-control window for new streams.
    /// Must not exceed <see cref="MaxInitialWindowSize"/>.
    /// </summary>
    public uint InitialWindowSize { get; set; } = InitialInitialWindowSize;

    /// <summary>
    /// MAX_FRAME_SIZE — largest frame payload the peer accepts. Must lie
    /// in [<see cref="MinMaxFrameSize"/>, <see cref="MaxMaxFrameSize"/>].
    /// </summary>
    public uint MaxFrameSize { get; set; } = InitialMaxFrameSize;

    /// <summary>MAX_HEADER_LIST_SIZE — advisory cap on uncompressed header list size.</summary>
    public uint MaxHeaderListSize { get; set; } = InitialMaxHeaderListSize;

    /// <summary>ENABLE_CONNECT_PROTOCOL — RFC 8441 extended CONNECT toggle. Must be 0 or 1.</summary>
    public uint EnableConnectProtocol { get; set; } = InitialEnableConnectProtocol;

    /// <summary>
    /// Validates a single peer-advertised setting per RFC 9113 §6.5.2
    /// and RFC 8441 §3 without applying it. Returns
    /// <see cref="Http2ErrorCode.NoError"/> on success.
    /// </summary>
    /// <remarks>
    /// Unknown parameter IDs return <see cref="Http2ErrorCode.NoError"/>
    /// per RFC 9113 §6.5.2 — they MUST be ignored, not rejected, so
    /// future settings defined by extensions do not break this endpoint.
    /// </remarks>
    public static (Http2ErrorCode ErrorCode, string? Message) Validate(in Http2PeerSetting setting)
    {
        switch (setting.Parameter)
        {
            case Http2SettingsParameter.SETTINGS_ENABLE_PUSH:
                if (setting.Value > 1)
                {
                    return (Http2ErrorCode.ProtocolError,
                        $"SETTINGS_ENABLE_PUSH must be 0 or 1; got {setting.Value}.");
                }
                break;

            case Http2SettingsParameter.SETTINGS_INITIAL_WINDOW_SIZE:
                if (setting.Value > MaxInitialWindowSize)
                {
                    return (Http2ErrorCode.FlowControlError,
                        $"SETTINGS_INITIAL_WINDOW_SIZE must not exceed {MaxInitialWindowSize}; got {setting.Value}.");
                }
                break;

            case Http2SettingsParameter.SETTINGS_MAX_FRAME_SIZE:
                if (setting.Value < MinMaxFrameSize || setting.Value > MaxMaxFrameSize)
                {
                    return (Http2ErrorCode.ProtocolError,
                        $"SETTINGS_MAX_FRAME_SIZE must be in [{MinMaxFrameSize}, {MaxMaxFrameSize}]; got {setting.Value}.");
                }
                break;

            case Http2SettingsParameter.SETTINGS_ENABLE_CONNECT_PROTOCOL:
                if (setting.Value > 1)
                {
                    return (Http2ErrorCode.ProtocolError,
                        $"SETTINGS_ENABLE_CONNECT_PROTOCOL must be 0 or 1; got {setting.Value}.");
                }
                break;
        }

        return (Http2ErrorCode.NoError, null);
    }

    /// <summary>
    /// Applies a peer setting to this snapshot. Caller MUST have already
    /// validated the setting via <see cref="Validate"/>. Unknown
    /// parameter IDs are silently ignored per RFC 9113 §6.5.2.
    /// </summary>
    public void Apply(in Http2PeerSetting setting)
    {
        switch (setting.Parameter)
        {
            case Http2SettingsParameter.SETTINGS_HEADER_TABLE_SIZE:
                HeaderTableSize = setting.Value;
                break;
            case Http2SettingsParameter.SETTINGS_ENABLE_PUSH:
                EnablePush = setting.Value;
                break;
            case Http2SettingsParameter.SETTINGS_MAX_CONCURRENT_STREAMS:
                MaxConcurrentStreams = setting.Value;
                break;
            case Http2SettingsParameter.SETTINGS_INITIAL_WINDOW_SIZE:
                InitialWindowSize = setting.Value;
                break;
            case Http2SettingsParameter.SETTINGS_MAX_FRAME_SIZE:
                MaxFrameSize = setting.Value;
                break;
            case Http2SettingsParameter.SETTINGS_MAX_HEADER_LIST_SIZE:
                MaxHeaderListSize = setting.Value;
                break;
            case Http2SettingsParameter.SETTINGS_ENABLE_CONNECT_PROTOCOL:
                EnableConnectProtocol = setting.Value;
                break;
            // Unknown parameter IDs are intentionally ignored (RFC 9113 §6.5.2).
        }
    }
}
