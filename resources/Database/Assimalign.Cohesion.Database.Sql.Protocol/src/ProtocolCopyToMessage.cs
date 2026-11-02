using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Protocol;

/// <summary>A SQL export request using the Execute envelope with an exact zero parameter count.</summary>
/// <param name="Statement">The COPY statement text.</param>
public sealed record ProtocolCopyToMessage(string Statement)
{
    /// <summary>Encodes the statement and zero parameter count.</summary>
    /// <returns>The export request payload.</returns>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public byte[] Encode()
    {
        ArgumentNullException.ThrowIfNull(Statement);
        var payload = new List<byte>();
        ProtocolPayload.WriteString(payload, Statement);
        ProtocolPayload.WriteInt32(payload, 0);
        return payload.ToArray();
    }

    /// <summary>Decodes an export request, rejecting parameters and trailing bytes.</summary>
    /// <param name="payload">The exact request payload.</param>
    /// <returns>The decoded statement.</returns>
    /// <exception cref="ProtocolException">The payload is malformed or contains parameters or trailing bytes.</exception>
    public static ProtocolCopyToMessage Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            int position = 0;
            string statement = ProtocolPayload.ReadString(payload, ref position);
            if (ProtocolPayload.ReadInt32(payload, ref position) != 0 || position != payload.Length)
            { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
            return new(statement);
        }
        catch (ProtocolException exception)
        { throw new ProtocolException(ErrorMessages.CopyStreamInvalid, exception); }
    }
}
