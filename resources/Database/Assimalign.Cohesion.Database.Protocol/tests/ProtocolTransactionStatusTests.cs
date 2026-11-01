using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

public sealed class ProtocolTransactionStatusTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Ready status carries exact transaction and retained-state bytes (#1717)")]
    [InlineData(ProtocolTransactionStatus.Idle, (byte)'I', false)]
    [InlineData(ProtocolTransactionStatus.Active, (byte)'T', false)]
    [InlineData(ProtocolTransactionStatus.Failed, (byte)'E', false)]
    [InlineData(ProtocolTransactionStatus.Idle, (byte)'I', true)]
    [InlineData(ProtocolTransactionStatus.Active, (byte)'T', true)]
    [InlineData(ProtocolTransactionStatus.Failed, (byte)'E', true)]
    public void Encode_Status_ShouldRoundTripExactlyTwoBytes(ProtocolTransactionStatus status, byte wire, bool retained)
    {
        // Arrange
        var message = new ProtocolTransactionStatusMessage(status, retained);
        byte[] expected = [wire, retained ? (byte)1 : (byte)0];

        // Act / Assert
        message.Encode().ShouldBe(expected);
        ProtocolTransactionStatusMessage.Decode(expected).ShouldBe(message);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Transaction status rejects malformed payloads (#1540)")]
    public void Decode_InvalidStatus_ShouldRejectUnknownTruncatedAndTrailingBytes()
    {
        // Arrange / Act / Assert
        foreach (byte[] payload in new byte[][] { [], [0], [(byte)'I'], [(byte)'X', 0], [(byte)'I', 2], [(byte)'I', 0, 0] })
        { Should.Throw<ProtocolException>(() => ProtocolTransactionStatusMessage.Decode(payload)).Message.ShouldContain("CK07002"); }
        Should.Throw<ProtocolException>(() => new ProtocolTransactionStatusMessage((ProtocolTransactionStatus)0, false).Encode());
    }
}
