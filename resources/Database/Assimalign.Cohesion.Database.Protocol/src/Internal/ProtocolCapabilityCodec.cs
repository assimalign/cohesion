using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

internal static class ProtocolCapabilityCodec
{
    internal const int MaxCount = 64;
    internal const int MaxPayloadLength = 16_384;

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128)
        {
            throw new ProtocolException(ErrorMessages.CapabilityListInvalid);
        }
        foreach (char character in name)
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            {
                throw new ProtocolException(ErrorMessages.CapabilityListInvalid);
            }
        }
    }

    internal static void Write(List<byte> buffer, IReadOnlyList<string> names)
    {
        if (names.Count > MaxCount) { throw new ProtocolException(ErrorMessages.CapabilityListInvalid); }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        ProtocolPayload.WriteUInt16(buffer, (ushort)names.Count);
        foreach (string name in names)
        {
            ValidateName(name);
            if (!unique.Add(name)) { throw new ProtocolException(ErrorMessages.CapabilityListInvalid); }
            ProtocolPayload.WriteString(buffer, name);
        }
    }

    internal static IReadOnlyList<string> Read(ReadOnlySpan<byte> payload, ref int position)
    {
        if (payload.Length - position > MaxPayloadLength) { throw new ProtocolException(ErrorMessages.CapabilityListInvalid); }
        int count = ProtocolPayload.ReadUInt16(payload, ref position);
        if (count > MaxCount) { throw new ProtocolException(ErrorMessages.CapabilityListInvalid); }
        var names = new string[count];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            names[index] = ProtocolPayload.ReadString(payload, ref position);
            ValidateName(names[index]);
            if (!unique.Add(names[index])) { throw new ProtocolException(ErrorMessages.CapabilityListInvalid); }
        }
        if (position != payload.Length) { throw new ProtocolException(ErrorMessages.CapabilityListInvalid); }
        return Array.AsReadOnly(names);
    }
}
