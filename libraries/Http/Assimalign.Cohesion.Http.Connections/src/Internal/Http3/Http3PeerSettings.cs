using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// The HTTP/3 settings advertised by the peer on its control stream
/// (RFC 9114 §7.2.4, RFC 9204 §5, RFC 9220 §3). Setting identifiers are
/// defined once in <see cref="Http3SettingId"/>. Unknown identifiers are
/// retained but ignored, per RFC 9114 §7.2.4.1.
/// </summary>
internal sealed class Http3PeerSettings
{
    private readonly Dictionary<long, long> _values = new();

    /// <summary>Records a setting value received from the peer.</summary>
    /// <param name="identifier">The setting identifier.</param>
    /// <param name="value">The setting value.</param>
    public void Set(long identifier, long value)
    {
        _values[identifier] = value;
    }

    /// <summary>Attempts to read a recorded setting value.</summary>
    /// <param name="identifier">The setting identifier.</param>
    /// <param name="value">The recorded value when present.</param>
    /// <returns><see langword="true"/> when the peer advertised the setting.</returns>
    public bool TryGet(long identifier, out long value)
    {
        return _values.TryGetValue(identifier, out value);
    }

    /// <summary>
    /// Gets whether the peer advertised support for the extended CONNECT
    /// protocol (RFC 9220) — i.e. SETTINGS_ENABLE_CONNECT_PROTOCOL = 1.
    /// </summary>
    public bool EnableConnectProtocolNegotiated
        => _values.TryGetValue(Http3SettingId.EnableConnectProtocol, out long value) && value == 1;
}
