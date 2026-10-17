using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol.Internal;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>The negotiated version and two-sided capability intersection.</summary>
/// <param name="Version">The agreed version.</param>
/// <param name="Capabilities">The agreed capability names, in server registration order.</param>
/// <exception cref="ArgumentNullException">The collection is null, including an init assignment.</exception>
public sealed record ProtocolReadyMessage(ProtocolVersion Version, IReadOnlyList<string> Capabilities)
{
    private IReadOnlyList<string> _capabilities = Capabilities ?? throw new ArgumentNullException(nameof(Capabilities));

    /// <summary>Gets or initializes the non-null collection.</summary>
    /// <exception cref="ArgumentNullException">The assigned collection is null.</exception>
    public IReadOnlyList<string> Capabilities
    {
        get => _capabilities;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _capabilities = value;
        }
    }
    /// <summary>Encodes Ready; the 2.0 baseline remains empty.</summary>
    /// <returns>The payload.</returns>
    /// <exception cref="ProtocolException">The capabilities are malformed or invalid for the version.</exception>
    public byte[] Encode()
    {
        if (Version == new ProtocolVersion(2, 0) && Capabilities.Count == 0) { return []; }
        if (Version.Major != 2 || Version.Minor < 1) { throw new ProtocolException(ErrorMessages.ReadyInvalid); }
        var buffer = new List<byte>();
        ProtocolPayload.WriteUInt16(buffer, Version.Major);
        ProtocolPayload.WriteUInt16(buffer, Version.Minor);
        ProtocolCapabilityCodec.Write(buffer, Capabilities);
        return buffer.ToArray();
    }

    /// <summary>Decodes Ready; an empty payload means 2.0 without capabilities.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The agreement.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static ProtocolReadyMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) { return new(new ProtocolVersion(2, 0), Array.Empty<string>()); }
        if (payload.Length > ProtocolCapabilityCodec.MaxPayloadLength) { throw new ProtocolException(ErrorMessages.ReadyInvalid); }
        int position = 0;
        var version = new ProtocolVersion(ProtocolPayload.ReadUInt16(payload, ref position), ProtocolPayload.ReadUInt16(payload, ref position));
        if (version.Major != 2 || version.Minor < 1) { throw new ProtocolException(ErrorMessages.ReadyInvalid); }
        return new(version, ProtocolCapabilityCodec.Read(payload, ref position));
    }
}
