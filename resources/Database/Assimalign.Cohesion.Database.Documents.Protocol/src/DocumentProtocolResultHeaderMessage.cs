using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Protocol.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Protocol;

/// <summary>Describes named projection rows or one bare JSON value per Document frame.</summary>
/// <param name="Columns">Names and shared type identities in projection order.</param>
/// <param name="WholeDocuments">Whether each frame is one bare JSON value (a lone SELECT *, or VALUE/RAW); requires exactly one column. Otherwise frames contain named projection objects.</param>
public sealed record DocumentProtocolResultHeaderMessage(IReadOnlyList<(string Name, byte Type)> Columns, bool WholeDocuments)
{
    /// <summary>Encodes the result header.</summary>
    /// <returns>The header payload.</returns>
    /// <exception cref="ProtocolException">The columns are invalid.</exception>
    public byte[] Encode()
    {
        if (Columns.Count == 0 || (WholeDocuments && Columns.Count != 1)) { throw new ProtocolException(ErrorMessages.ResultColumnsInvalid); }
        var buffer = new List<byte> { WholeDocuments ? (byte)1 : (byte)0 };
        ProtocolPayload.WriteInt32(buffer, Columns.Count);
        foreach (var (name, type) in Columns)
        {
            if (string.IsNullOrEmpty(name) || !Enum.IsDefined((DatabaseType)type)) { throw new ProtocolException(ErrorMessages.ResultColumnInvalid); }
            ProtocolPayload.WriteString(buffer, name);
            buffer.Add(type);
        }
        return buffer.ToArray();
    }

    /// <summary>Decodes the result header.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The validated header.</returns>
    /// <exception cref="ProtocolException">The payload is invalid or has trailing bytes.</exception>
    public static DocumentProtocolResultHeaderMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] > 1) { throw new ProtocolException(ErrorMessages.ResultKindInvalid); }
        bool documents = payload[0] == 1;
        int position = 1;
        int count = ProtocolPayload.ReadInt32(payload, ref position);
        if (count <= 0 || count > (payload.Length - position) / 5 || (documents && count != 1)) { throw new ProtocolException(ErrorMessages.ResultColumnsInvalid); }
        var columns = new (string Name, byte Type)[count];
        for (int index = 0; index < count; index++)
        {
            string name = ProtocolPayload.ReadString(payload, ref position);
            if (string.IsNullOrEmpty(name) || position >= payload.Length || !Enum.IsDefined((DatabaseType)payload[position]))
            {
                throw new ProtocolException(ErrorMessages.ResultColumnInvalid);
            }
            columns[index] = (name, payload[position++]);
        }
        DocumentProtocolWire.EnsureEnd(payload, position);
        return new(columns, documents);
    }
}
