using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Protocol;

/// <summary>
/// Opens a streamed result: the column names and type identifiers of the rows that
/// follow. Row frames (<see cref="SqlProtocolMessageType.ResultRow"/>) carry each row
/// as shared tuple-codec bytes, one typed component per column.
/// </summary>
/// <param name="Columns">The columns: name, shared type identity byte and declared length (null if unbounded).</param>
public sealed record ProtocolResultHeaderMessage(IReadOnlyList<(string Name, byte Type, int? MaxLength)> Columns)
{
    private const int formatMarker = -2;

    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="SqlProtocolMessageType.ResultHeader"/> frame.</returns>
    public byte[] Encode()
    {
        var buffer = new List<byte>(64);
        ProtocolPayload.WriteInt32(buffer, formatMarker); // SQL result-header format 2; no legacy reader.
        ProtocolPayload.WriteInt32(buffer, Columns.Count);

        foreach (var (name, type, maxLength) in Columns)
        {
            ProtocolPayload.WriteString(buffer, name);
            buffer.Add(type);
            ProtocolPayload.WriteInt32(buffer, maxLength ?? -1);
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
        if (ProtocolPayload.ReadInt32(payload, ref position) != formatMarker)
        {
            throw new ProtocolException("Unsupported SQL result-header format; rebuild the client and server together.");
        }
        int count = ProtocolPayload.ReadInt32(payload, ref position);

        if (count < 0 || count > (payload.Length - position) / 9)
        {
            throw new ProtocolException("Malformed payload: invalid column count.");
        }

        var columns = new List<(string, byte, int?)>(count);

        for (int i = 0; i < count; i++)
        {
            string name = ProtocolPayload.ReadString(payload, ref position);

            if (position >= payload.Length)
            {
                throw new ProtocolException("Malformed payload: truncated column type.");
            }

            byte type = payload[position++];
            int length = ProtocolPayload.ReadInt32(payload, ref position);
            if (length < -1 || length == 0)
            {
                throw new ProtocolException("Malformed payload: invalid column length.");
            }
            columns.Add((name, type, length == -1 ? null : length));
        }

        if (position != payload.Length)
        {
            throw new ProtocolException("Malformed payload: trailing result-header bytes.");
        }
        return new ProtocolResultHeaderMessage(columns);
    }
}
