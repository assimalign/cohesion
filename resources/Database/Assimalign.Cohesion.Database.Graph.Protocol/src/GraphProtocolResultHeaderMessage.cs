using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Protocol;

/// <summary>
/// Opens a streamed result: the column names and type identifiers of the rows that
/// follow. Row frames (<see cref="GraphProtocolMessageType.ResultRow"/>) carry each row
/// as shared tuple-codec bytes for scalar columns. With graph.composite-values, bit 7 of
/// a column type marks possible composites: tag 0 precedes a scalar tuple component; tag 1
/// precedes a little-endian Int32 length and GraphValueCodec component. Lists decode to
/// object?[] and maps to read-only IReadOnlyDictionary&lt;string, object?&gt;. Values are limited
/// to depth 64, 65,536 values and 16 MiB; each row also fits 16 MiB.
/// </summary>
/// <param name="Columns">The columns: name plus the shared type identity byte, with bit 7 marking negotiated composite columns.</param>
public sealed record GraphProtocolResultHeaderMessage(IReadOnlyList<(string Name, byte Type)> Columns)
{
    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="GraphProtocolMessageType.ResultHeader"/> frame.</returns>
    public byte[] Encode()
    {
        var buffer = new List<byte>(64);
        ProtocolPayload.WriteInt32(buffer, Columns.Count);

        foreach (var (name, type) in Columns)
        {
            ProtocolPayload.WriteString(buffer, name);
            buffer.Add(type);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a result-header payload.
    /// </summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static GraphProtocolResultHeaderMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        int count = ProtocolPayload.ReadInt32(payload, ref position);

        if (count < 0 || count > (payload.Length - position) / 5)
        {
            throw new ProtocolException(ErrorMessages.ResultHeaderMessageDecodeProtocol);
        }

        var columns = new List<(string, byte)>(count);

        for (int i = 0; i < count; i++)
        {
            string name = ProtocolPayload.ReadString(payload, ref position);

            if (position >= payload.Length)
            {
                throw new ProtocolException(ErrorMessages.ResultHeaderMessageDecodeProtocol2);
            }

            columns.Add((name, payload[position++]));
        }

        return new GraphProtocolResultHeaderMessage(columns);
    }
}
