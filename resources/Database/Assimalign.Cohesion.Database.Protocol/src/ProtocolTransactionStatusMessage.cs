using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>The two-byte Ready postamble negotiated by transaction-status; startup Ready is unchanged.</summary>
/// <param name="Status">The explicit transaction state after the statement completes.</param>
/// <param name="HasRetainedState">Whether session state requires the family reset statement before pool reuse.</param>
public sealed record ProtocolTransactionStatusMessage(ProtocolTransactionStatus Status, bool HasRetainedState)
{
    /// <summary>Encodes the statement Ready payload.</summary>
    /// <returns>The transaction status followed by a zero or one retained-state byte.</returns>
    /// <exception cref="ProtocolException">The status is invalid.</exception>
    public byte[] Encode()
    {
        Validate(Status);
        return [(byte)Status, HasRetainedState ? (byte)1 : (byte)0];
    }

    /// <summary>Decodes an exact two-byte statement Ready payload.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The reported state.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static ProtocolTransactionStatusMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2 || payload[1] > 1) { throw new ProtocolException(ErrorMessages.TransactionStatusInvalid); }
        var status = (ProtocolTransactionStatus)payload[0];
        Validate(status);
        return new(status, payload[1] != 0);
    }

    private static void Validate(ProtocolTransactionStatus status)
    {
        if (status is not (ProtocolTransactionStatus.Idle or ProtocolTransactionStatus.Active or ProtocolTransactionStatus.Failed))
        { throw new ProtocolException(ErrorMessages.TransactionStatusInvalid); }
    }
}
