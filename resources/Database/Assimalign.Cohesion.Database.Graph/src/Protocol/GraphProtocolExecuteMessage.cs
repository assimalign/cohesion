using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A graph request carrying GQL text and named scalar parameters. Both catalog
/// Execute and ExecutePaths use this payload with the shared scalar tuple codec.
/// </summary>
/// <param name="Statement">The GQL statement text.</param>
/// <param name="Parameters">The encoded parameter tuples: name → tuple-codec component bytes.</param>
public sealed record GraphProtocolExecuteMessage(string Statement, IReadOnlyDictionary<string, byte[]> Parameters)
{
    /// <summary>
    /// An execute message without parameters.
    /// </summary>
    /// <param name="statement">The statement text.</param>
    /// <returns>The message.</returns>
    public static GraphProtocolExecuteMessage Create(string statement)
        => new(statement, new Dictionary<string, byte[]>());

    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="GraphProtocolMessageType.Execute"/> frame.</returns>
    public byte[] Encode()
    {
        var buffer = new List<byte>(128);
        ProtocolPayload.WriteString(buffer, Statement);
        ProtocolPayload.WriteInt32(buffer, Parameters.Count);

        foreach (var (name, encoded) in Parameters)
        {
            ProtocolPayload.WriteString(buffer, name);
            ProtocolPayload.WriteInt32(buffer, encoded.Length);
            buffer.AddRange(encoded);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes an execute payload.
    /// </summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static GraphProtocolExecuteMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string statement = ProtocolPayload.ReadString(payload, ref position);
        int count = ProtocolPayload.ReadInt32(payload, ref position);

        if (count < 0 || count > (payload.Length - position) / 8)
        {
            throw new ProtocolException("Malformed payload: invalid parameter count.");
        }

        var parameters = new Dictionary<string, byte[]>(count);

        for (int i = 0; i < count; i++)
        {
            string name = ProtocolPayload.ReadString(payload, ref position);
            int length = ProtocolPayload.ReadInt32(payload, ref position);

            if (length < 0 || length > payload.Length - position)
            {
                throw new ProtocolException("Malformed payload: invalid parameter length.");
            }

            parameters[name] = payload.Slice(position, length).ToArray();
            position += length;
        }

        return new GraphProtocolExecuteMessage(statement, parameters);
    }
}
