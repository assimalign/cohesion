using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>An OQL statement with named parameters represented by a JSON object.</summary>
/// <param name="Statement">The OQL statement.</param>
/// <param name="Parameters">A UTF-8 JSON object containing named parameter values.</param>
public sealed record DocumentProtocolExecuteMessage(string Statement, ReadOnlyMemory<byte> Parameters)
{
    /// <summary>Creates a request without parameters.</summary>
    /// <param name="statement">The OQL statement.</param>
    /// <returns>The request with an empty parameter object.</returns>
    public static DocumentProtocolExecuteMessage Create(string statement) => new(statement, "{}"u8.ToArray());

    /// <summary>Encodes the request payload.</summary>
    /// <returns>The encoded statement and JSON parameter object.</returns>
    /// <exception cref="ProtocolException">The parameter payload is not a valid JSON object.</exception>
    public byte[] Encode()
    {
        DocumentProtocolJson.Validate(Parameters.Span, requireObject: true);
        var buffer = new List<byte>();
        ProtocolPayload.WriteString(buffer, Statement);
        ProtocolPayload.WriteInt32(buffer, Parameters.Length);
        buffer.AddRange(Parameters.ToArray());
        return buffer.ToArray();
    }

    /// <summary>Decodes a request payload.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded request.</returns>
    /// <exception cref="ProtocolException">The payload is malformed or has trailing bytes.</exception>
    public static DocumentProtocolExecuteMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string statement = ProtocolPayload.ReadString(payload, ref position);
        int length = ProtocolPayload.ReadInt32(payload, ref position);
        if (length < 0 || length != payload.Length - position)
        {
            throw new ProtocolException("Malformed document parameter length.");
        }
        var parameters = payload[position..];
        DocumentProtocolJson.Validate(parameters, requireObject: true);
        return new(statement, parameters.ToArray());
    }
}
