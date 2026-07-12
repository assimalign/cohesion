using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Opens a streamed result: the column names and type identifiers of the rows that
/// follow. Row frames (<see cref="ProtocolMessageType.ResultRow"/>) carry each row
/// as shared tuple-codec bytes, one typed component per column.
/// </summary>
/// <param name="Columns">The columns: name plus the shared type identity byte.</param>
public sealed record ProtocolResultHeaderMessage(IReadOnlyList<(string Name, byte Type)> Columns)
{
    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="ProtocolMessageType.ResultHeader"/> frame.</returns>
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
    public static ProtocolResultHeaderMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        int count = ProtocolPayload.ReadInt32(payload, ref position);

        if (count < 0)
        {
            throw new ProtocolException("Malformed payload: negative column count.");
        }

        var columns = new List<(string, byte)>(count);

        for (int i = 0; i < count; i++)
        {
            string name = ProtocolPayload.ReadString(payload, ref position);

            if (position >= payload.Length)
            {
                throw new ProtocolException("Malformed payload: truncated column type.");
            }

            columns.Add((name, payload[position++]));
        }

        return new ProtocolResultHeaderMessage(columns);
    }
}
