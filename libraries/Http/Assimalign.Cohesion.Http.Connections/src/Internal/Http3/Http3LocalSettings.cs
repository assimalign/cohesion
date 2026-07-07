using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// Encodes the server's HTTP/3 SETTINGS frame payload — the identifier/value
/// pairs the server advertises as the first frame on its outbound control
/// stream (RFC 9114 §6.2.1 / §7.2.4).
/// </summary>
/// <remarks>
/// The advertised posture mirrors the HTTP/2 transport's initial SETTINGS:
/// SETTINGS_ENABLE_CONNECT_PROTOCOL = 1 (RFC 9220 §3) tells peers they may
/// bootstrap extended CONNECT. QPACK_MAX_TABLE_CAPACITY (RFC 9204 §5) is the
/// server's decoder capacity: 0 (the default) states explicitly that the QPACK
/// dynamic table is disabled, and a non-zero value opts in to the dynamic table
/// and is advertised alongside QPACK_BLOCKED_STREAMS. All values are intentional
/// statements of intent, so the server emits them rather than relying on the
/// peer to assume the RFC defaults.
/// </remarks>
internal static class Http3LocalSettings
{
    /// <summary>
    /// Serializes the server's SETTINGS payload as a sequence of QUIC
    /// variable-length integer identifier/value pairs (RFC 9114 §7.2.4).
    /// </summary>
    /// <param name="qpackOptions">The server's advertised QPACK capacity and blocked-stream limit.</param>
    /// <returns>The encoded SETTINGS payload octets.</returns>
    public static byte[] EncodePayload(Http3QPackOptions qpackOptions)
    {
        using MemoryStream payload = new();

        // RFC 9204 §5 — QPACK_MAX_TABLE_CAPACITY. Zero states the
        // dynamic-table-disabled posture explicitly; a non-zero value is the
        // decoder capacity the server offers the peer's encoder.
        QuicVariableLengthInteger.Write(payload, Http3SettingId.QPackMaxTableCapacity);
        QuicVariableLengthInteger.Write(payload, qpackOptions.MaxTableCapacity);

        // RFC 9204 §5 — QPACK_BLOCKED_STREAMS. Only advertised when the dynamic
        // table is enabled; it is meaningless (and defaults to 0) otherwise.
        if (qpackOptions.DynamicTableEnabled)
        {
            QuicVariableLengthInteger.Write(payload, Http3SettingId.QPackBlockedStreams);
            QuicVariableLengthInteger.Write(payload, qpackOptions.MaxBlockedStreams);
        }

        // RFC 9220 §3 — SETTINGS_ENABLE_CONNECT_PROTOCOL = 1, matching the
        // HTTP/2 transport's RFC 8441/9220 posture so peers may initiate
        // extended CONNECT (CONNECT + :protocol) over HTTP/3.
        QuicVariableLengthInteger.Write(payload, Http3SettingId.EnableConnectProtocol);
        QuicVariableLengthInteger.Write(payload, 1);

        return payload.ToArray();
    }
}
