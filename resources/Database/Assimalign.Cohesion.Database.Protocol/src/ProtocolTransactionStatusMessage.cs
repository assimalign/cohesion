using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>The one-byte Ready postamble negotiated by transaction-status; startup Ready is unchanged.</summary>
/// <param name="Status">The explicit transaction state after the statement completes.</param>
public sealed record ProtocolTransactionStatusMessage(ProtocolTransactionStatus Status)
{
    /// <summary>Encodes the statement Ready payload.</summary>
    /// <returns>The one-byte status.</returns>
    /// <exception cref="ProtocolException">The status is invalid.</exception>
    public byte[] Encode()
    {
        Validate(Status);
        return [(byte)Status];
    }

    /// <summary>Decodes an exact one-byte statement Ready payload.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The reported state.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static ProtocolTransactionStatusMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1) { throw new ProtocolException(ErrorMessages.TransactionStatusInvalid); }
        var status = (ProtocolTransactionStatus)payload[0];
        Validate(status);
        return new(status);
    }

    private static void Validate(ProtocolTransactionStatus status)
    {
        if (status is not (ProtocolTransactionStatus.Idle or ProtocolTransactionStatus.Active or ProtocolTransactionStatus.Failed))
        { throw new ProtocolException(ErrorMessages.TransactionStatusInvalid); }
    }
}
