using System;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The EDNS Cookie option &#8211; RFC 7873. Carries a lightweight transaction binding to
/// defend against off-path spoofing and reflection-style attacks.
/// </summary>
/// <remarks>
/// <para>
/// The option is exactly 8 octets in queries (the 64-bit Client Cookie) and 16&#8211;40
/// octets in responses (Client Cookie + 8&#8211;32 octet Server Cookie). Cohesion
/// represents both halves with separate byte arrays so callers can manage them explicitly.
/// </para>
/// </remarks>
public sealed class DnsEdnsCookieOption : DnsEdnsOption
{
    private readonly byte[] _clientCookie;
    private readonly byte[]? _serverCookie;

    /// <summary>
    /// Initializes a new EDNS Cookie option.
    /// </summary>
    /// <param name="clientCookie">Exactly 8 octets supplied by the client.</param>
    /// <param name="serverCookie">8&#8211;32 octets supplied by the server in responses; null
    /// or empty for query-side cookies.</param>
    public DnsEdnsCookieOption(ReadOnlySpan<byte> clientCookie, ReadOnlySpan<byte> serverCookie = default)
        : base(DnsEdnsOptionCode.Cookie)
    {
        if (clientCookie.Length != 8)
        {
            throw new ArgumentException("Client cookie must be exactly 8 octets.", nameof(clientCookie));
        }
        if (serverCookie.Length != 0 && (serverCookie.Length < 8 || serverCookie.Length > 32))
        {
            throw new ArgumentException("Server cookie, when present, must be 8..32 octets.", nameof(serverCookie));
        }
        _clientCookie = clientCookie.ToArray();
        _serverCookie = serverCookie.Length == 0 ? null : serverCookie.ToArray();
    }

    /// <summary>The 8-octet Client Cookie.</summary>
    public ReadOnlySpan<byte> ClientCookie => _clientCookie;

    /// <summary>The Server Cookie, or an empty span when only a Client Cookie is present.</summary>
    public ReadOnlySpan<byte> ServerCookie => _serverCookie ?? Array.Empty<byte>();

    /// <summary>True when this cookie carries a Server Cookie.</summary>
    public bool HasServerCookie => _serverCookie is not null;

    /// <inheritdoc />
    internal override void WritePayload(ref DnsWireWriter writer)
    {
        writer.WriteBytes(_clientCookie);
        if (_serverCookie is not null)
        {
            writer.WriteBytes(_serverCookie);
        }
    }

    internal static DnsEdnsCookieOption ReadPayload(ref DnsWireReader reader, int payloadLength)
    {
        if (payloadLength != 8 && (payloadLength < 16 || payloadLength > 40))
        {
            DnsException.ThrowMalformed(
                $"Cookie option payload must be 8 (client only) or 16..40 (client+server) octets; got {payloadLength}");
        }
        ReadOnlySpan<byte> client = reader.ReadBytes(8);
        ReadOnlySpan<byte> server = payloadLength == 8
            ? default
            : reader.ReadBytes(payloadLength - 8);
        return new DnsEdnsCookieOption(client, server);
    }
}
