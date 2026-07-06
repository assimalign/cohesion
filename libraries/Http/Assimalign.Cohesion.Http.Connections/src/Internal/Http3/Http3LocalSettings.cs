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
/// bootstrap extended CONNECT, and QPACK_MAX_TABLE_CAPACITY = 0 (RFC 9204 §5)
/// states explicitly that the QPACK dynamic table is disabled. Both values are
/// intentional statements of intent, so the server emits them rather than
/// relying on the peer to assume the RFC defaults.
/// </remarks>
internal static class Http3LocalSettings
{
    /// <summary>
    /// Serializes the server's SETTINGS payload as a sequence of QUIC
    /// variable-length integer identifier/value pairs (RFC 9114 §7.2.4).
    /// </summary>
    /// <returns>The encoded SETTINGS payload octets.</returns>
    public static byte[] EncodePayload()
    {
        using MemoryStream payload = new();

        // RFC 9204 §5 — QPACK_MAX_TABLE_CAPACITY = 0 states the
        // dynamic-table-disabled posture explicitly (the QPACK codec runs with
        // the dynamic table off; see the QPACK design notes).
        QuicVariableLengthInteger.Write(payload, Http3SettingId.QPackMaxTableCapacity);
        QuicVariableLengthInteger.Write(payload, 0);

        // RFC 9220 §3 — SETTINGS_ENABLE_CONNECT_PROTOCOL = 1, matching the
        // HTTP/2 transport's RFC 8441/9220 posture so peers may initiate
        // extended CONNECT (CONNECT + :protocol) over HTTP/3.
        QuicVariableLengthInteger.Write(payload, Http3SettingId.EnableConnectProtocol);
        QuicVariableLengthInteger.Write(payload, 1);

        return payload.ToArray();
    }
}
